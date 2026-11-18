using System.Net.Sockets;
using AwesomeAssertions;
using Xunit;

namespace Elarion.Tests.Infrastructure;

/// <summary>
/// Regression tests for the Docker-gated fixtures' container lifecycle: under a full parallel run the engine refused
/// connections, which skipped whole classes, leaked containers, and turned a refused teardown into a class cleanup
/// failure that failed every already-passed test of the class.
/// </summary>
public sealed class TestContainerLifecycleTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DisposeAsync_WhenTheEngineKeepsRefusingTheDelete_DoesNotThrow() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        var container = new FakeContainer { DisposeFailures = int.MaxValue };

        await lifecycle.DisposeAsync(container);

        container.DisposeCalls.Should().Be(3);
    }

    [Fact]
    public async Task DisposeAsync_RetriesARefusedDelete() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        var container = new FakeContainer { DisposeFailures = 1 };

        await lifecycle.DisposeAsync(container);

        container.DisposeCalls.Should().Be(2);
        container.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_WithoutAContainer_IsANoOp() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);

        await lifecycle.DisposeAsync(null);
    }

    [Fact]
    public async Task StartAsync_WhenAStartIsRefused_DisposesThatContainerAndRetriesWithAFreshOne() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        var built = new List<FakeContainer>();

        var started = await lifecycle.StartAsync(
            () => Track(built, new FakeContainer()),
            (container, _) => built.Count == 1 ? throw Refused() : Task.CompletedTask,
            Ct);

        built.Should().HaveCount(2);
        started.Should().BeSameAs(built[1]);
        built[0].Disposed.Should().BeTrue("a failed start must not leak the created container");
        built[1].Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WhenEveryAttemptFails_DisposesEachContainerAndThrowsTheLastFailure() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        var built = new List<FakeContainer>();

        var act = () => lifecycle.StartAsync(
            () => Track(built, new FakeContainer()),
            (_, _) => throw Refused(),
            Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
        built.Should().HaveCount(3);
        built.Should().OnlyContain(container => container.Disposed);
    }

    [Fact]
    public async Task StartAsync_WhenAnEngineRequestTimesOut_Retries() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        var built = new List<FakeContainer>();

        var started = await lifecycle.StartAsync(
            () => Track(built, new FakeContainer()),
            (_, _) => built.Count == 1
                ? throw new TaskCanceledException("The operation was canceled.")
                : Task.CompletedTask,
            Ct);

        started.Should().BeSameAs(built[1]);
        built[0].Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WhenTheCallerCancels_DoesNotRetry() {
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var built = new List<FakeContainer>();

        var act = () => lifecycle.StartAsync(
            () => Track(built, new FakeContainer()),
            (_, _) => {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        built.Should().ContainSingle().Which.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task StartAsync_WhenBuildFails_DoesNotRetry() {
        // Build() is where Testcontainers detects that there is no Docker engine at all; the fixture turns that
        // into a skip, so retrying it would only delay every skip.
        var lifecycle = new TestContainerLifecycle(1, 3, TimeSpan.Zero);
        var builds = 0;

        var act = () => lifecycle.StartAsync<FakeContainer>(
            () => {
                builds++;
                throw new InvalidOperationException("Docker is either not running or misconfigured.");
            },
            (_, _) => Task.CompletedTask,
            Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        builds.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_NeverRunsMoreLifecyclesConcurrentlyThanTheLimit() {
        const int limit = 2;
        var lifecycle = new TestContainerLifecycle(limit, 1, TimeSpan.Zero);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var limitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = 0;
        var maxRunning = 0;

        var starts = Enumerable.Range(0, 6).Select(_ => lifecycle.StartAsync(
            () => new FakeContainer(),
            async (_, ct) => {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref maxRunning, now);
                if (now == limit) limitReached.TrySetResult();
                await release.Task.WaitAsync(ct);
                Interlocked.Decrement(ref running);
            },
            Ct)).ToList();

        await limitReached.Task.WaitAsync(Ct);
        release.SetResult();
        await Task.WhenAll(starts);

        maxRunning.Should().Be(limit);
    }

    private static FakeContainer Track(List<FakeContainer> built, FakeContainer container) {
        built.Add(container);
        return container;
    }

    // The shape the engine produced under load: Docker.DotNet wraps the refused socket connect.
    private static HttpRequestException Refused() {
        return new HttpRequestException("Connection failed.", new SocketException((int)SocketError.ConnectionRefused));
    }

    private static void InterlockedMax(ref int target, int value) {
        var current = Volatile.Read(ref target);
        while (value > current) {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private sealed class FakeContainer : IAsyncDisposable {
        public int DisposeFailures { get; init; }

        public int DisposeCalls { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync() {
            DisposeCalls++;
            if (DisposeCalls <= DisposeFailures) throw Refused();
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
