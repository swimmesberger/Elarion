using System.Threading.Channels;
using AwesomeAssertions;
using Elarion.Buffering;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.Buffering;

/// <summary>
/// The ADR-0055 conflation contract: latest wins per key, at most one emit per MinInterval per key
/// (leading edge immediate, trailing edge for the quiet tail — never ending on a stale value), per-key
/// serialization against a slow publish target, and flush-on-dispose.
/// </summary>
public sealed class KeyedConflaterTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task TheFirstPostOfAnIdleKey_EmitsImmediately() {
        var emissions = Channel.CreateUnbounded<(string Key, int Value)>();
        await using var conflater = new KeyedConflater<string, int>(
            async (key, value, _) => await emissions.Writer.WriteAsync((key, value), TestToken),
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = new FakeTimeProvider() });

        conflater.Post("a", 1);

        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(("a", 1));
    }

    [Fact]
    public async Task PostsInsideTheWindow_ConflateToTheLatest_EmittedOnTheTrailingEdge() {
        var time = new FakeTimeProvider();
        var emissions = Channel.CreateUnbounded<(string Key, int Value)>();
        await using var conflater = new KeyedConflater<string, int>(
            async (key, value, _) => await emissions.Writer.WriteAsync((key, value), TestToken),
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = time });

        conflater.Post("a", 1);
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(("a", 1));

        // Inside the window: 2 and 3 conflate; only the newest survives to the trailing edge.
        conflater.Post("a", 2);
        conflater.Post("a", 3);
        emissions.Reader.TryRead(out _).Should().BeFalse();

        time.Advance(Interval);
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(("a", 3));
    }

    [Fact]
    public async Task AQuietKey_GoesIdle_AndTheNextPostEmitsImmediatelyAgain() {
        var time = new FakeTimeProvider();
        var emissions = Channel.CreateUnbounded<(string Key, int Value)>();
        await using var conflater = new KeyedConflater<string, int>(
            async (key, value, _) => await emissions.Writer.WriteAsync((key, value), TestToken),
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = time });

        conflater.Post("a", 1);
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(("a", 1));

        // Two full windows pass with nothing pending: the key retires; a fresh post is a leading edge.
        time.Advance(Interval);
        time.Advance(Interval);
        conflater.Post("a", 2);
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(("a", 2));
        emissions.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Keys_RateLimitIndependently() {
        var emissions = Channel.CreateUnbounded<(string Key, int Value)>();
        await using var conflater = new KeyedConflater<string, int>(
            async (key, value, _) => await emissions.Writer.WriteAsync((key, value), TestToken),
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = new FakeTimeProvider() });

        conflater.Post("a", 1);
        conflater.Post("b", 2);

        var first = await emissions.Reader.ReadAsync(TestToken);
        var second = await emissions.Reader.ReadAsync(TestToken);
        new[] { first, second }.Should().BeEquivalentTo(new[] { ("a", 1), ("b", 2) });
    }

    [Fact]
    public async Task ASlowPublish_NeverOverlaps_AndTheNewestValueFollowsItsCompletion() {
        var time = new FakeTimeProvider();
        var emissions = Channel.CreateUnbounded<int>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = 0;

        await using var conflater = new KeyedConflater<string, int>(
            async (_, value, _) => {
                if (Interlocked.Increment(ref published) == 1) {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                await emissions.Writer.WriteAsync(value, TestToken);
            },
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = time });

        conflater.Post("a", 1);
        await firstStarted.Task.WaitAsync(TestToken);

        // The window elapses while the first publish is still in flight; the conflated latest must wait
        // for completion (per-key serialization), then emit without needing another window.
        conflater.Post("a", 2);
        conflater.Post("a", 3);
        time.Advance(Interval);
        emissions.Reader.TryRead(out _).Should().BeFalse();

        releaseFirst.SetResult();
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(1);
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(3);
    }

    [Fact]
    public async Task APublishFailure_IsDroppedToTheErrorCallback_AndTheKeyKeepsWorking() {
        var time = new FakeTimeProvider();
        var errors = Channel.CreateUnbounded<(Exception Exception, string Key, int Value)>();
        var emissions = Channel.CreateUnbounded<int>();
        var calls = 0;

        await using var conflater = new KeyedConflater<string, int>(
            async (_, value, _) => {
                if (Interlocked.Increment(ref calls) == 1) {
                    throw new InvalidOperationException("hub down");
                }

                await emissions.Writer.WriteAsync(value, TestToken);
            },
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = time },
            (exception, key, value) => errors.Writer.TryWrite((exception, key, value)));

        conflater.Post("a", 1);
        var (error, key, value) = await errors.Reader.ReadAsync(TestToken);
        error.Should().BeOfType<InvalidOperationException>();
        (key, value).Should().Be(("a", 1));

        time.Advance(Interval);
        conflater.Post("a", 2);
        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(2);
    }

    [Fact]
    public async Task AThrowingErrorCallback_NeverWedgesTheKey() {
        var time = new FakeTimeProvider();
        var emissions = Channel.CreateUnbounded<int>();
        var calls = 0;

        await using var conflater = new KeyedConflater<string, int>(
            async (_, value, _) => {
                if (Interlocked.Increment(ref calls) == 1) {
                    throw new InvalidOperationException("hub down");
                }

                await emissions.Writer.WriteAsync(value, TestToken);
            },
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = time },
            (_, _, _) => throw new InvalidOperationException("logger down too"));

        // The failed emit's error callback throws; the key must still leave the publishing state and
        // emit the conflated pending value on the trailing edge.
        conflater.Post("a", 1);
        conflater.Post("a", 2);
        time.Advance(Interval);

        (await emissions.Reader.ReadAsync(TestToken)).Should().Be(2);
    }

    [Fact]
    public async Task Dispose_FlushesEveryPendingLatest_ThenDropsLatePosts() {
        var time = new FakeTimeProvider();
        var emissions = Channel.CreateUnbounded<(string Key, int Value)>();
        var conflater = new KeyedConflater<string, int>(
            async (key, value, _) => await emissions.Writer.WriteAsync((key, value), TestToken),
            new KeyedConflaterOptions { MinInterval = Interval, TimeProvider = time });

        conflater.Post("a", 1);
        conflater.Post("b", 10);
        await emissions.Reader.ReadAsync(TestToken); // the two leading emits
        await emissions.Reader.ReadAsync(TestToken);

        // Pending values inside open windows: dispose must publish them without waiting for the timers.
        conflater.Post("a", 2);
        conflater.Post("b", 20);
        await conflater.DisposeAsync();

        var flushed = new List<(string, int)> {
            await emissions.Reader.ReadAsync(TestToken),
            await emissions.Reader.ReadAsync(TestToken),
        };
        flushed.Should().BeEquivalentTo(new[] { ("a", 2), ("b", 20) });

        conflater.Post("a", 3);
        emissions.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Options_AreValidated() {
        Func<string, int, CancellationToken, ValueTask> publish = (_, _, _) => ValueTask.CompletedTask;

        var zeroInterval = () => new KeyedConflater<string, int>(
            publish, new KeyedConflaterOptions { MinInterval = TimeSpan.Zero });
        zeroInterval.Should().Throw<ArgumentOutOfRangeException>();

        await using var defaults = new KeyedConflater<string, int>(publish);
        defaults.Post("a", 1);
    }
}
