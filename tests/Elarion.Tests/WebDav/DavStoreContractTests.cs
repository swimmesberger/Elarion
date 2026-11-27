using System.Text;
using AwesomeAssertions;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>
/// The behaviour every <see cref="IDavCollectionStore"/> must have (spec 0006 FR-006, FR-007, FR-020, FR-021), run against
/// the in-memory store and the EF store on PostgreSQL.
/// </summary>
public abstract class DavStoreContractTests {
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Creates a service provider whose scopes hold a store; the time provider drives journal retention.</summary>
    protected abstract ServiceProvider CreateServices(FakeTimeProvider time, Action<WebDavOptions>? options = null);

    protected virtual void SkipUnlessAvailable() { }

    protected static DavCollectionAddress NewAddress() => new("thing", "s" + Guid.NewGuid().ToString("N")[..8], "k");

    protected static DavObjectContent Content(string uid, string text, DateTimeOffset? start = null, DateTimeOffset? end = null) {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new DavObjectContent {
            Name = uid + ".ics", Uid = uid, Content = bytes, ContentType = "text/plain", ETag = DavETag.Compute(bytes),
            RangeStart = start, RangeEnd = end,
        };
    }

    protected static async Task<T> WithStoreAsync<T>(ServiceProvider services, Func<IDavCollectionStore, Task<T>> action) {
        await using var scope = services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<IDavCollectionStore>());
    }

    protected static Task<DavCollectionState> StateAsync(ServiceProvider services, DavCollectionAddress address) =>
        WithStoreAsync(services, async store => (await store.GetStatesAsync([address], Ct))[0]);

    [Fact]
    public async Task PublishingTheSameEventAgainUnchanged_DoesNotChangeTheSyncToken() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var address = NewAddress();

        (await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "A"), Ct).AsTask())).Should().Be(DavPublishOutcome.Added);
        var token = (await StateAsync(services, address)).SyncToken;
        (await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "A"), Ct).AsTask())).Should().Be(DavPublishOutcome.Unchanged);

        (await StateAsync(services, address)).SyncToken.Should().Be(token);
    }

    [Fact]
    public async Task PublishingWithAChangedStart_ChangesTheTokenAndETag_AndASyncFromTheOldTokenReportsIt() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var address = NewAddress();
        await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "DTSTART:1"), Ct).AsTask());
        var before = await StateAsync(services, address);

        (await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "DTSTART:2"), Ct).AsTask())).Should().Be(DavPublishOutcome.Changed);

        var after = await StateAsync(services, address);
        after.SyncToken.Should().NotBe(before.SyncToken);
        var changes = await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, before.SyncSeq, Ct).AsTask());
        changes!.Changes.Should().ContainSingle().Which.Should().Be(new DavChange("a.ics", DavChangeKind.Changed, after.SyncSeq));
        var stored = await WithStoreAsync(services, async s => await s.GetObjectsAsync(address.Id, ["a.ics"], true, Ct).SingleAsync(Ct));
        stored.ETag.Should().Be(Content("a", "DTSTART:2").ETag);
        Encoding.UTF8.GetString(stored.Content.Span).Should().Be("DTSTART:2");
    }

    [Fact]
    public async Task RemovingAnEvent_IsReportedAsRemoved() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var address = NewAddress();
        await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "A"), Ct).AsTask());
        var before = await StateAsync(services, address);

        (await WithStoreAsync(services, s => s.RemoveAsync(address, "a.ics", Ct).AsTask())).Should().BeTrue();
        (await WithStoreAsync(services, s => s.RemoveAsync(address, "a.ics", Ct).AsTask())).Should().BeFalse();

        var changes = await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, before.SyncSeq, Ct).AsTask());
        changes!.Changes.Should().ContainSingle().Which.Kind.Should().Be(DavChangeKind.Removed);
        (await StateAsync(services, address)).SyncSeq.Should().Be(before.SyncSeq + 1);
    }

    [Fact]
    public async Task ReplacingA300EventCalendarWith299OneChanged_RecordsExactlyOneChangeAndOneRemoval() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var address = NewAddress();
        var initial = await WithStoreAsync(services, s => s.ReplaceAsync(address,
            Enumerable.Range(0, 300).Select(i => Content("e" + i, "v1-" + i)).ToAsyncEnumerable(), Ct).AsTask());
        initial.Should().Be(new DavReplaceOutcome(300, 0, 0));
        var before = await StateAsync(services, address);

        var outcome = await WithStoreAsync(services, s => s.ReplaceAsync(address,
            Enumerable.Range(0, 299).Select(i => Content("e" + i, i == 7 ? "v2-7" : "v1-" + i)).ToAsyncEnumerable(), Ct).AsTask());

        outcome.Should().Be(new DavReplaceOutcome(0, 1, 1));
        var changes = await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, before.SyncSeq, Ct).AsTask());
        changes!.Changes.Select(static c => (c.Name, c.Kind)).Should().BeEquivalentTo(new[] {
            ("e7.ics", DavChangeKind.Changed), ("e299.ics", DavChangeKind.Removed),
        });
        var unchanged = await WithStoreAsync(services, s => s.ReplaceAsync(address,
            Enumerable.Range(0, 299).Select(i => Content("e" + i, i == 7 ? "v2-7" : "v1-" + i)).ToAsyncEnumerable(), Ct).AsTask());
        unchanged.HasChanges.Should().BeFalse();
        (await StateAsync(services, address)).SyncSeq.Should().Be(changes.CurrentSeq);
    }

    [Fact]
    public async Task TheJournal_PrunesBeyondItsRowCapWhileTheCollectionChanges() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow), o => o.MaxJournalRowsPerCollection = 3);
        var address = NewAddress();
        for (var i = 0; i < 10; i++) await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "v" + i), Ct).AsTask());

        var state = await StateAsync(services, address);

        state.SyncSeq.Should().Be(10);
        state.LowWater.Should().Be(7);
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 6, Ct).AsTask())).Should().BeNull();
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 7, Ct).AsTask()))!.Changes.Should().HaveCount(3);
    }

    [Fact]
    public async Task TheJournal_PrunesChangesOlderThanTheRetention() {
        SkipUnlessAvailable();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var services = CreateServices(time, o => o.JournalRetention = TimeSpan.FromDays(30));
        var address = NewAddress();
        await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "1"), Ct).AsTask());
        await WithStoreAsync(services, s => s.PublishAsync(address, Content("b", "1"), Ct).AsTask());
        time.Advance(TimeSpan.FromDays(31));

        await WithStoreAsync(services, s => s.PublishAsync(address, Content("c", "1"), Ct).AsTask());

        (await StateAsync(services, address)).LowWater.Should().Be(2);
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 1, Ct).AsTask())).Should().BeNull();
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 2, Ct).AsTask()))!.Changes.Should().ContainSingle();
    }

    [Fact]
    public async Task Candidates_AreSelectedByTheirRecordedSpan() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var address = NewAddress();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await WithStoreAsync(services, s => s.ReplaceAsync(address, new[] {
            Content("timeless", "x"),
            Content("past", "x", t0.AddDays(-30), t0.AddDays(-29)),
            Content("week", "x", t0.AddDays(2), t0.AddDays(2).AddHours(1)),
            Content("endless", "x", t0.AddYears(-1), null),
            Content("future", "x", t0.AddDays(30), t0.AddDays(31)),
        }.ToAsyncEnumerable(), Ct).AsTask());

        var names = await WithStoreAsync(services, async s =>
            await s.GetCandidatesAsync(address.Id, t0, t0.AddDays(7), Ct).Select(static o => o.Name).ToListAsync(Ct));

        names.Should().Equal("endless.ics", "timeless.ics", "week.ics");
    }

    [Fact]
    public async Task ANewCollection_StartsAtZeroAndAnEmptyTokenIsValid() {
        SkipUnlessAvailable();
        await using var services = CreateServices(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var address = NewAddress();

        (await StateAsync(services, address)).SyncSeq.Should().Be(0);
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 0, Ct).AsTask()))!.Changes.Should().BeEmpty();
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 3, Ct).AsTask())).Should().BeNull();
        await WithStoreAsync(services, s => s.PublishAsync(address, Content("a", "1"), Ct).AsTask());
        (await WithStoreAsync(services, s => s.GetChangesAsync(address.Id, 0, Ct).AsTask()))!.Changes.Should().ContainSingle();
    }

    /// <summary>
    /// Publishes, changes, removes and replaces objects in random order while a simulated client syncs at random points
    /// through the dispatcher, with pruning in between: the client's copy always equals the store after its sync (plan
    /// 0006, "Sync properties").
    /// </summary>
    [Fact]
    public async Task RandomizedPublishingAndSyncing_KeepsTheClientsCopyEqualToTheStore() {
        SkipUnlessAvailable();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var services = CreateServices(time, o => o.MaxJournalRowsPerCollection = 25);
        var address = NewAddress();
        var dispatcher = services.GetRequiredService<DavDispatcher>();
        var protocol = (TestThingsProtocol)services.GetRequiredService<IDavProtocol>();
        protocol.Show("u1", address.Source, address.Key);
        var random = new Random(4711);
        var client = new Dictionary<string, string>(StringComparer.Ordinal);
        string? token = null;
        var resyncs = 0;

        for (var step = 0; step < 400; step++) {
            var roll = random.Next(100);
            var uid = "o" + random.Next(30);
            if (roll < 50) {
                await WithStoreAsync(services, s => s.PublishAsync(address, Content(uid, "v" + random.Next(3)), Ct).AsTask());
            }
            else if (roll < 70) {
                await WithStoreAsync(services, s => s.RemoveAsync(address, uid + ".ics", Ct).AsTask());
            }
            else if (roll < 75) {
                var set = Enumerable.Range(0, 30).Where(_ => random.Next(2) == 0).Select(i => Content("o" + i, "v" + random.Next(3))).ToList();
                await WithStoreAsync(services, s => s.ReplaceAsync(address, set.ToAsyncEnumerable(), Ct).AsTask());
            }
            else {
                if (random.Next(10) == 0) time.Advance(TimeSpan.FromDays(1));
                var (newToken, fresh) = await SyncAsync(dispatcher, services, address, token, client, random.Next(4) == 0 ? random.Next(1, 6) : null);
                if (fresh) resyncs++;
                token = newToken;
                var truth = await WithStoreAsync(services, async s =>
                    await s.ListMembersAsync(address.Id, Ct).ToDictionaryAsync(static m => m.Name, static m => m.ETag, StringComparer.Ordinal, Ct));
                client.Should().BeEquivalentTo(truth, $"after step {step}");
            }
        }

        resyncs.Should().BeGreaterThan(0, "the journal cap forces at least one full resync");
    }

    // One client sync as DAVx5 does it: sync-collection from the token, 403 valid-sync-token → start over, 507 → continue.
    private static async Task<(string Token, bool Resynced)> SyncAsync(DavDispatcher dispatcher, ServiceProvider services,
        DavCollectionAddress address, string? token, Dictionary<string, string> client, int? limit) {
        var resynced = false;
        while (true) {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = new TestDavUser("u1");
            var body = DavTestHarness.SyncCollection(token, limit);
            var response = await dispatcher.HandleAsync(new DavRequest {
                Method = "REPORT", Path = "/things/u1/" + address.Segment + "/", HrefBase = "/dav",
                Body = new MemoryStream(Encoding.UTF8.GetBytes(body)),
            }, scope.ServiceProvider, Ct);
            using var output = new MemoryStream();
            await response.WriteBodyAsync(output, Ct);
            var result = new DavTestResponse(response.StatusCode, response.Headers, response.ContentType, output.ToArray());
            if (result.Status == 403) {
                token = null;
                client.Clear();
                resynced = true;
                continue;
            }

            result.Status.Should().Be(207);
            var truncated = false;
            foreach (var (href, element) in result.Responses) {
                var status = element.Child(DavNames.D.Status)?.Text ?? "";
                if (status.Contains("507", StringComparison.Ordinal)) {
                    truncated = true;
                    continue;
                }

                var name = href[(href.LastIndexOf('/') + 1)..];
                if (status.Contains("404", StringComparison.Ordinal)) client.Remove(name);
                else client[name] = result.Prop(href, DavNames.D.GetETag)!.Text;
            }

            token = result.SyncToken!;
            if (!truncated) return (token, resynced);
        }
    }
}

/// <summary>The store contract on the in-memory store.</summary>
public sealed class InMemoryDavStoreContractTests : DavStoreContractTests {
    protected override ServiceProvider CreateServices(FakeTimeProvider time, Action<WebDavOptions>? options = null) {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddElarionWebDav(configure: options);
        services.AddElarionInMemoryDavStore();
        services.AddSingleton<IDavProtocol, TestThingsProtocol>();
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<Elarion.Abstractions.Identity.ICurrentUser>(p => p.GetRequiredService<TestUserAccessor>().User);
        return services.BuildServiceProvider();
    }
}
