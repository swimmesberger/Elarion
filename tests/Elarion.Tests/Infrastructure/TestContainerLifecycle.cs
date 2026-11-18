using DotNet.Testcontainers.Containers;
using Xunit;

namespace Elarion.Tests.Infrastructure;

/// <summary>
/// Starts and disposes the Docker-gated fixtures' Testcontainers through one process-wide gate.
/// </summary>
/// <remarks>
/// Every integration class owns a class fixture with its own container, and xUnit runs classes in parallel, so a
/// full run fires dozens of container lifecycles (create, start, readiness polling, delete) at the Docker API at
/// once. A single-VM engine (a Podman machine, Docker Desktop) answers that burst with refused connections. Before
/// this gate, a refused start silently skipped the whole class and leaked the half-created container (there is no
/// reaper when Ryuk is disabled), and a refused delete surfaced as a class cleanup failure that marked every
/// already-passed test of the class as failed — which is why a class failed in the full run yet passed alone.
/// Bounding the concurrent lifecycles, retrying a failed start with a fresh container, and treating teardown as
/// best-effort keeps the suite's outcome independent of how many fixtures happen to run in parallel.
/// </remarks>
internal sealed class TestContainerLifecycle {
    private readonly SemaphoreSlim _gate;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryDelay;

    public TestContainerLifecycle(int maxConcurrentLifecycles, int maxAttempts, TimeSpan retryDelay) {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentLifecycles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        _gate = new SemaphoreSlim(maxConcurrentLifecycles, maxConcurrentLifecycles);
        _maxAttempts = maxAttempts;
        _retryDelay = retryDelay;
    }

    /// <summary>The gate every Docker-gated fixture in the test assembly shares.</summary>
    /// <remarks>Four concurrent lifecycles keep a 7-CPU Podman machine responsive while still overlapping the
    /// slow part of a start (PostgreSQL initdb + readiness polling) across fixtures. Five attempts with a linear
    /// backoff (2 + 4 + 6 + 8 s) ride out an engine that is also busy with another suite on the same machine.</remarks>
    public static TestContainerLifecycle Shared { get; } = new(4, 5, TimeSpan.FromSeconds(2));

    /// <summary>
    /// Builds and starts a container, retrying a failed start with a freshly built container. A failure of
    /// <paramref name="build"/> itself (Testcontainers validates the Docker endpoint there and throws
    /// <c>DockerUnavailableException</c> when there is no engine) is never retried, so the caller's
    /// skip-when-Docker-is-unavailable path stays immediate.
    /// </summary>
    public Task<TContainer> StartAsync<TContainer>(Func<TContainer> build) where TContainer : IContainer {
        return StartAsync(build, static (container, ct) => container.StartAsync(ct),
            TestContext.Current.CancellationToken);
    }

    /// <inheritdoc cref="StartAsync{TContainer}(Func{TContainer})" />
    public async Task<TContainer> StartAsync<TContainer>(
        Func<TContainer> build,
        Func<TContainer, CancellationToken, Task> start,
        CancellationToken cancellationToken) where TContainer : IAsyncDisposable {
        await _gate.WaitAsync(cancellationToken);
        try {
            for (var attempt = 1;; attempt++) {
                var container = build();
                try {
                    await start(container, cancellationToken);
                    return container;
                }
                // Only the caller's own cancellation stops the retries: an engine-side request timeout also
                // surfaces as an OperationCanceledException (Docker.DotNet's HttpClient), and it is transient.
                catch (Exception ex) when (attempt < _maxAttempts && !cancellationToken.IsCancellationRequested) {
                    TestContext.Current.SendDiagnosticMessage(
                        $"Test container start attempt {attempt}/{_maxAttempts} failed, retrying: {ex.Message}");
                    await DisposeWithRetryAsync(container);
                    await Task.Delay(_retryDelay * attempt, cancellationToken);
                }
                catch {
                    // A failed start can leave a created container behind; nothing else holds a reference to it.
                    await DisposeWithRetryAsync(container);
                    throw;
                }
            }
        }
        finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// Disposes (stops and deletes) a container, retrying a refused engine call and never throwing: a fixture's
    /// teardown runs after its tests have already passed, so an engine hiccup there must not fail the class.
    /// </summary>
    public async ValueTask DisposeAsync(IAsyncDisposable? container) {
        if (container is null) return;

        // CancellationToken.None: teardown must still run when the test run is being cancelled.
        await _gate.WaitAsync(CancellationToken.None);
        try {
            await DisposeWithRetryAsync(container);
        }
        finally {
            _gate.Release();
        }
    }

    private async ValueTask DisposeWithRetryAsync(IAsyncDisposable container) {
        for (var attempt = 1;; attempt++) {
            try {
                await container.DisposeAsync();
                return;
            }
            catch (Exception ex) when (attempt < _maxAttempts) {
                TestContext.Current.SendDiagnosticMessage(
                    $"Test container disposal attempt {attempt}/{_maxAttempts} failed, retrying: {ex.Message}");
                await Task.Delay(_retryDelay * attempt, CancellationToken.None);
            }
            catch (Exception ex) {
                TestContext.Current.SendDiagnosticMessage(
                    $"Test container disposal failed after {_maxAttempts} attempts; the container may be left behind: {ex.Message}");
                return;
            }
        }
    }
}
