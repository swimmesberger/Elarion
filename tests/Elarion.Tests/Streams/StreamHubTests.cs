using AwesomeAssertions;
using Elarion.Streams;
using Xunit;

namespace Elarion.Tests.Streams;

/// <summary>
/// The ADR-0052 hub contract: publish order is subscriber-observed order, replay-then-live is atomic,
/// resume replays from the ring (a gap shows as a sequence jump), overflow follows the subscriber's own
/// strategy, and Complete/Fail end every subscription.
/// </summary>
public sealed class StreamHubTests {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Publishes_AreObservedInOrder_WithContiguousSequences() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 0 });
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions { Replay = StreamReplay.None });

        for (var i = 1; i <= 5; i++) await hub.PublishAsync(i * 10, TestToken);

        hub.Complete();

        var items = await Collect(subscription);
        items.Should().Equal(
            new StreamItem<int>(1, 10), new StreamItem<int>(2, 20), new StreamItem<int>(3, 30),
            new StreamItem<int>(4, 40), new StreamItem<int>(5, 50));
    }

    [Fact]
    public async Task LatestReplay_GreetsANewSubscriberWithTheCurrentValue() {
        var hub = new StreamHub<string>();
        await hub.PublishAsync("old", TestToken);
        await hub.PublishAsync("current", TestToken);

        var subscription = hub.SubscribeSequenced();
        hub.Complete();

        var items = await Collect(subscription);
        items.Should().ContainSingle().Which.Should().Be(new StreamItem<string>(2, "current"));
    }

    [Fact]
    public async Task LatestReplay_OnAnUnprimedHub_GreetsWithNothing() {
        var hub = new StreamHub<string>();
        var subscription = hub.SubscribeSequenced();
        hub.Complete();

        (await Collect(subscription)).Should().BeEmpty();
    }

    [Fact]
    public async Task LatestReplay_WithRetentionDisabled_GreetsWithNothing() {
        // ReplayCapacity 0 = no retention at all: Latest greets with nothing even after publishes.
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 0 });
        await hub.PublishAsync(1, TestToken);

        var subscription = hub.SubscribeSequenced();
        hub.Complete();

        (await Collect(subscription)).Should().BeEmpty();
    }

    [Fact]
    public async Task AvailableReplay_DeliversTheWholeRing_ThenLive() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 2 });
        await hub.PublishAsync(1, TestToken);
        await hub.PublishAsync(2, TestToken);
        await hub.PublishAsync(3, TestToken); // evicts 1 from the ring

        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions { Replay = StreamReplay.Available });
        await hub.PublishAsync(4, TestToken);
        hub.Complete();

        var items = await Collect(subscription);
        items.Select(static i => i.Sequence).Should().Equal(2, 3, 4);
    }

    [Fact]
    public async Task Resume_ReplaysOnlyNewerRetainedElements() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 16 });
        for (var i = 1; i <= 5; i++) await hub.PublishAsync(i, TestToken);

        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions { ResumeAfterSequence = 3 });
        hub.Complete();

        var items = await Collect(subscription);
        items.Select(static i => i.Sequence).Should().Equal(4, 5);
    }

    [Fact]
    public async Task Resume_GapBeyondTheRing_DeliversWhatRemains_AsASequenceJump() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 2 });
        for (var i = 1; i <= 6; i++) await hub.PublishAsync(i, TestToken);

        // The subscriber saw 1 and asks for everything after; only 5 and 6 survive in the ring.
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions { ResumeAfterSequence = 1 });
        hub.Complete();

        var items = await Collect(subscription);
        items.Select(static i => i.Sequence).Should().Equal(5, 6);
    }

    [Fact]
    public async Task ReplayThenLive_IsAtomic_NoDuplicateAndNoGapAroundSubscribe() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 512 });
        var publisher = Task.Run(async () => {
            for (var i = 1; i <= 200; i++) await hub.PublishAsync(i, TestToken);
        }, TestToken);

        // Subscribe mid-publish: everything retained plus everything after must be contiguous.
        await Task.Delay(5, TestToken);
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions {
            Replay = StreamReplay.Available, BufferCapacity = 512
        });
        await publisher;
        hub.Complete();

        var sequences = (await Collect(subscription)).Select(static i => i.Sequence).ToList();
        sequences.Should().NotBeEmpty();
        sequences.Should().Equal(Enumerable.Range((int)sequences[0], sequences.Count).Select(static i => (long)i));
        sequences[^1].Should().Be(200);
    }

    [Fact]
    public async Task DropOldest_LossIsVisibleAsASequenceJump() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 0 });
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions {
            Replay = StreamReplay.None, BufferCapacity = 2, Overflow = StreamOverflowMode.DropOldest
        });

        for (var i = 1; i <= 10; i++) await hub.PublishAsync(i, TestToken);

        hub.Complete();

        var sequences = (await Collect(subscription)).Select(static i => i.Sequence).ToList();
        sequences.Should().HaveCount(2).And.BeInAscendingOrder();
        sequences[^1].Should().Be(10); // latest wins; the drop is a visible jump, not a silent hole
    }

    [Fact]
    public async Task WaitOverflow_BackpressuresThePublisher_UntilTheSubscriberReads() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 0 });
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions {
            Replay = StreamReplay.None, BufferCapacity = 1, Overflow = StreamOverflowMode.Wait
        });

        await hub.PublishAsync(1, TestToken);
        var second = hub.PublishAsync(2, TestToken).AsTask();
        await Task.Delay(50, TestToken);
        second.IsCompleted.Should().BeFalse("the buffer is full and the subscriber has not read yet");

        var items = new List<StreamItem<int>>();
        await foreach (var item in subscription.WithCancellation(TestToken)) {
            items.Add(item);
            if (items.Count == 1) {
                await second; // reading frees the slot; the pending publish must now complete
                hub.Complete();
            }
        }

        items.Select(static i => i.Value).Should().Equal(1, 2);
    }

    [Fact]
    public async Task CancelOverflow_FailsTheLaggingSubscriber_AndNeverDelaysThePublisher() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 0 });
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions {
            Replay = StreamReplay.None, BufferCapacity = 1, Overflow = StreamOverflowMode.Cancel
        });

        await hub.PublishAsync(1, TestToken);
        await hub.PublishAsync(2, TestToken); // overflows the unread buffer → subscriber is cancelled

        var act = async () => await Collect(subscription);
        (await act.Should().ThrowAsync<StreamLaggedException>())
            .WithMessage("*ResumeAfterSequence*");
        hub.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task WaitOverflow_UnsubscribingWhileThePublisherIsBlocked_WakesThePublisher() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 0 });
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions {
            Replay = StreamReplay.None, BufferCapacity = 1, Overflow = StreamOverflowMode.Wait
        });

        var enumerator = subscription.GetAsyncEnumerator(TestToken);
        await hub.PublishAsync(1, TestToken);
        (await enumerator.MoveNextAsync()).Should().BeTrue(); // start enumerating so disposal unsubscribes
        await hub.PublishAsync(2, TestToken); // refills the freed slot
        var blocked = hub.PublishAsync(3, TestToken).AsTask();
        await Task.Delay(50, TestToken);
        blocked.IsCompleted.Should().BeFalse("the buffer is full and the subscriber has not read");

        await enumerator.DisposeAsync(); // unsubscribe without draining the buffer

        // The blocked publish must wake instead of suspending forever while holding the publish gate.
        await blocked;
        hub.SubscriberCount.Should().Be(0);
        await hub.PublishAsync(4, TestToken); // and later publishes go through
    }

    [Fact]
    public async Task CancelOverflow_LagMessage_ReportsTheEffectiveCapacity_WhenAReplayBurstWidenedIt() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 4 });
        for (var i = 1; i <= 4; i++) await hub.PublishAsync(i, TestToken);

        // The replay burst (4) widens the effective buffer beyond the configured capacity (1).
        var subscription = hub.SubscribeSequenced(new StreamSubscribeOptions {
            Replay = StreamReplay.Available, BufferCapacity = 1, Overflow = StreamOverflowMode.Cancel
        });
        await hub.PublishAsync(5, TestToken); // overflows the widened, unread buffer

        var act = async () => await Collect(subscription);
        (await act.Should().ThrowAsync<StreamLaggedException>())
            .WithMessage("*more than 4 elements*");
    }

    [Fact]
    public async Task Fail_SurfacesTheErrorToEverySubscriber_AndToLateSubscribers() {
        var hub = new StreamHub<int>();
        var live = hub.SubscribeSequenced(new StreamSubscribeOptions { Replay = StreamReplay.None });
        hub.Fail(new InvalidDataException("feed broke"));

        var actLive = async () => await Collect(live);
        (await actLive.Should().ThrowAsync<InvalidDataException>()).WithMessage("feed broke");

        var late = hub.SubscribeSequenced(new StreamSubscribeOptions { Replay = StreamReplay.None });
        var actLate = async () => await Collect(late);
        (await actLate.Should().ThrowAsync<InvalidDataException>()).WithMessage("feed broke");
    }

    [Fact]
    public async Task SubscribeAfterComplete_StillReplays_ThenEnds() {
        var hub = new StreamHub<int>(new StreamHubOptions { ReplayCapacity = 4 });
        await hub.PublishAsync(7, TestToken);
        hub.Complete();

        var items = await Collect(
            hub.SubscribeSequenced(new StreamSubscribeOptions { Replay = StreamReplay.Available }));
        items.Should().ContainSingle().Which.Value.Should().Be(7);
    }

    [Fact]
    public async Task PublishAfterComplete_Throws() {
        var hub = new StreamHub<int>();
        hub.Complete();

        var act = async () => await hub.PublishAsync(1, TestToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DisposingTheEnumerator_Unsubscribes() {
        var hub = new StreamHub<int>();
        await hub.PublishAsync(1, TestToken);
        var subscription = hub.SubscribeSequenced();
        hub.SubscriberCount.Should().Be(1);

        await foreach (var _ in subscription.WithCancellation(TestToken)) break; // dispose mid-stream

        hub.SubscriberCount.Should().Be(0);
    }

    [Fact]
    public async Task Subscribe_ReturnsPlainValues_WithTheSameSemantics() {
        var hub = new StreamHub<string>();
        await hub.PublishAsync("greeting", TestToken);
        var subscription = hub.Subscribe();
        await hub.PublishAsync("live", TestToken);
        hub.Complete();

        var items = new List<string>();
        await foreach (var item in subscription.WithCancellation(TestToken)) items.Add(item);

        items.Should().Equal("greeting", "live");
    }

    private static async Task<List<StreamItem<T>>> Collect<T>(IAsyncEnumerable<StreamItem<T>> source) {
        var items = new List<StreamItem<T>>();
        await foreach (var item in source.WithCancellation(TestToken)) items.Add(item);

        return items;
    }
}
