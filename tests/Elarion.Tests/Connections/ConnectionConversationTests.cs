using AwesomeAssertions;
using Elarion.Connections;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// Covers the conversation helpers: the inbox (buffer-then-wait and wait-then-post, predicate matching that
/// leaves non-matching messages for later waiters, timeout, completion faulting pending and future waiters,
/// bounded drop-oldest) and the keyed pending-request map (round-trip, unknown/late replies, timeout,
/// duplicate keys, teardown fail-all).
/// </summary>
public sealed class ConnectionConversationTests {
    [Fact]
    public async Task Inbox_DeliversToWaiter_AndFromBuffer() {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new ConnectionInbox<string>();

        var wait = inbox.WaitAsync(ct: ct);
        inbox.Post("first");
        (await wait).Should().Be("first");

        inbox.Post("second");
        (await inbox.WaitAsync(ct: ct)).Should().Be("second");
    }

    [Fact]
    public async Task Inbox_PredicateSkipsNonMatching_WhichStaysBuffered() {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new ConnectionInbox<string>();

        var waitForB = inbox.WaitAsync(m => m.StartsWith('b'), ct: ct);
        inbox.Post("alpha");
        inbox.Post("bravo");

        (await waitForB).Should().Be("bravo");
        (await inbox.WaitAsync(ct: ct)).Should().Be("alpha");
    }

    [Fact]
    public async Task Inbox_Timeout_Faults() {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new ConnectionInbox<string>();

        var wait = async () => await inbox.WaitAsync(timeout: TimeSpan.FromMilliseconds(20), ct: ct);

        await wait.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Inbox_Complete_FaultsPendingAndFutureWaiters() {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new ConnectionInbox<string>();
        var pending = inbox.WaitAsync(ct: ct);

        inbox.Complete();

        await ((Func<Task>)(() => pending)).Should().ThrowAsync<ConnectionInboxCompletedException>();
        await ((Func<Task>)(() => inbox.WaitAsync(ct: ct))).Should().ThrowAsync<ConnectionInboxCompletedException>();
        inbox.Post("ignored");
    }

    [Fact]
    public async Task Inbox_BoundedBuffer_DropsOldest() {
        var ct = TestContext.Current.CancellationToken;
        var inbox = new ConnectionInbox<string>(2);
        inbox.Post("one");
        inbox.Post("two");
        inbox.Post("three");

        (await inbox.WaitAsync(ct: ct)).Should().Be("two");
        (await inbox.WaitAsync(ct: ct)).Should().Be("three");
    }

    [Fact]
    public async Task PendingRequests_RoundTrip_AndLateRepliesReportFalse() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();

        var wait = requests.WaitAsync(7, ct: ct);
        requests.TryComplete(7, "reply").Should().BeTrue();
        (await wait).Should().Be("reply");

        requests.TryComplete(7, "again").Should().BeFalse();
        requests.TryComplete(99, "unsolicited").Should().BeFalse();
    }

    [Fact]
    public async Task PendingRequests_Timeout_WithdrawsTheRegistration() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();

        var wait = async () => await requests.WaitAsync(1, TimeSpan.FromMilliseconds(20), ct);
        await wait.Should().ThrowAsync<TimeoutException>();

        requests.TryComplete(1, "too late").Should().BeFalse();
        // The key is free again after the timeout — a retry can re-register it.
        var retry = requests.WaitAsync(1, ct: ct);
        requests.TryComplete(1, "second attempt").Should().BeTrue();
        (await retry).Should().Be("second attempt");
    }

    [Fact]
    public async Task PendingRequests_DuplicateKey_Throws() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();
        _ = requests.WaitAsync(5, ct: ct);

        var duplicate = async () => await requests.WaitAsync(5, ct: ct);

        await duplicate.Should().ThrowAsync<InvalidOperationException>();
        requests.TryComplete(5, "unblock").Should().BeTrue();
    }

    [Fact]
    public async Task PendingRequests_FailAll_FaultsInFlightAndFuture() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();
        var inFlight = requests.WaitAsync(1, ct: ct);

        requests.FailAll(new InvalidOperationException("link down"));

        await ((Func<Task>)(() => inFlight)).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("link down");
        await ((Func<Task>)(() => requests.WaitAsync(2, ct: ct))).Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("link down");
    }

    [Fact]
    public async Task SendAndWait_RegistersBeforeTheSend_SoAFastReplyIsNeverLost() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();

        // The reply lands while the send is still "on the wire" — registration-first means it completes
        // the already-pending entry instead of reporting an unsolicited reply.
        var roundTrip = requests.SendAndWaitAsync(3, _ => {
            requests.TryComplete(3, "immediate").Should().BeTrue();
            return ValueTask.CompletedTask;
        }, ct: ct);

        (await roundTrip).Should().Be("immediate");
    }

    [Fact]
    public async Task SendAndWait_FailedSend_WithdrawsTheRegistration_AndTheKeyIsImmediatelyReusable() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();

        var failed = async () => await requests.SendAndWaitAsync(
            9, _ => throw new IOException("send leg broke"), ct: ct);
        await failed.Should().ThrowAsync<IOException>();

        // Nothing waits for a reply that can never come, and the key is free for the retry.
        requests.TryComplete(9, "stale").Should().BeFalse();
        var retry = requests.SendAndWaitAsync(9, _ => ValueTask.CompletedTask, ct: ct);
        requests.TryComplete(9, "second attempt").Should().BeTrue();
        (await retry).Should().Be("second attempt");
    }

    [Fact]
    public async Task SendAndWait_ReplyRacingAFailedSend_WinsOverTheSendFault() {
        var ct = TestContext.Current.CancellationToken;
        var requests = new ConnectionPendingRequests<int, string>();

        // The request reached the peer and was answered even though the send leg reported a failure —
        // the reply already settled the registration, so it is handed over instead of lost.
        var result = await requests.SendAndWaitAsync(4, _ => {
            requests.TryComplete(4, "answered anyway").Should().BeTrue();
            throw new IOException("reported after the fact");
        }, ct: ct);

        result.Should().Be("answered anyway");
    }
}
