using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Npgsql;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Cross-instance change-notification tests for the PostgreSQL <c>LISTEN/NOTIFY</c> settings change source:
/// two independent source+listener pairs over one database — the multi-node topology — where a write observed
/// by one node must fire the other node's watch tokens. Skips when Docker is unavailable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlSettingsChangeSourceIntegrationTests(PostgreSqlSettingsStoreFixture fixture)
    : IClassFixture<PostgreSqlSettingsStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan FireTimeout = TimeSpan.FromSeconds(15);

    // Long enough for a wrongly-sent notification to arrive, short enough to keep the suite fast.
    private static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(750);

    private static string UniqueKey() {
        return $"app:{Guid.NewGuid():N}";
    }

    [Fact]
    public async Task WriteOnOneNode_FiresWatcherOnOtherNode() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var nodeA = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);
        await using var nodeB = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);
        var key = UniqueKey();
        var fired = WaitForFire(nodeB.Source.Watch(SettingsScope.Global));

        await using var context = fixture.CreateContext();
        var store = CreateStore(context, nodeA.Options);
        await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

        await fired.WaitAsync(FireTimeout, Ct);
    }

    [Fact]
    public async Task PublishOnOneNode_FiresMatchingPrefixWatchersEverywhere() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var nodeA = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);
        await using var nodeB = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);

        var matching = WaitForFire(nodeB.Source.Watch(SettingsScope.Global, "app:theme"));
        var otherPrefix = WaitForFire(nodeB.Source.Watch(SettingsScope.Global, "jobs"));
        var otherScope = WaitForFire(nodeB.Source.Watch(SettingsScope.User("someone-else")));
        var local = WaitForFire(nodeA.Source.Watch(SettingsScope.Global, "app:theme"));

        nodeA.Source.Publish(SettingsScope.Global, "app:theme:accent");

        await matching.WaitAsync(FireTimeout, Ct);
        // The publishing node observes its own change through the same loop-back path.
        await local.WaitAsync(FireTimeout, Ct);

        await Task.Delay(QuietWindow, Ct);
        otherPrefix.IsCompleted.Should().BeFalse();
        otherScope.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task TransactionalWrite_NotifiesOnlyOnCommit() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var node = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);
        var key = UniqueKey();
        var fired = WaitForFire(node.Source.Watch(SettingsScope.Global));

        await using var context = fixture.CreateContext();
        var store = CreateStore(context, node.Options);
        await using (var transaction = await context.Database.BeginTransactionAsync(Ct)) {
            await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);

            // NOTIFY rides the caller's transaction: nothing is delivered before commit.
            await Task.Delay(QuietWindow, Ct);
            fired.IsCompleted.Should().BeFalse();

            await transaction.CommitAsync(Ct);
        }

        await fired.WaitAsync(FireTimeout, Ct);
    }

    [Fact]
    public async Task RolledBackWrite_NeverNotifies() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var node = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);
        var key = UniqueKey();
        var fired = WaitForFire(node.Source.Watch(SettingsScope.Global));

        await using (var context = fixture.CreateContext()) {
            var store = CreateStore(context, node.Options);
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await store.SetAsync(SettingsScope.Global, key, "v1", cancellationToken: Ct);
            await transaction.RollbackAsync(Ct);
        }

        await Task.Delay(QuietWindow, Ct);
        fired.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task FirstListenEstablishment_FiresPreexistingWatchers() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        // Watch BEFORE the listener starts: notifications sent in that window are lost (the first connect
        // attempt can back off), so the first establishment itself must make watchers re-read — not only
        // reconnects.
        var options = new PostgreSqlSettingsChangeOptions();
        await using var source = new PostgreSqlSettingsChangeSource(
            NpgsqlDataSource.Create(fixture.ConnectionString),
            true,
            options,
            NullLogger<PostgreSqlSettingsChangeSource>.Instance);
        var fired = WaitForFire(source.Watch(SettingsScope.Global));

        var listener = new PostgreSqlSettingsChangeListener(
            source, options, NullLogger<PostgreSqlSettingsChangeListener>.Instance);
        await listener.StartAsync(Ct);
        try {
            await listener.Listening.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await fired.WaitAsync(FireTimeout, Ct);
        }
        finally {
            await listener.StopAsync(CancellationToken.None);
            listener.Dispose();
        }
    }

    [Fact]
    public async Task TerminatedListenConnection_ReconnectsFiresAllWatchers_AndStaysLive() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var node = await ChangeNode.StartAsync(fixture.ConnectionString, Ct);
        var fired = WaitForFire(node.Source.Watch(SettingsScope.Global));

        await TerminateListenBackendsAsync(fixture.ConnectionString, Ct);

        // The re-established LISTEN makes every watcher re-read (changes may have been missed) …
        await fired.WaitAsync(FireTimeout, Ct);

        // … and notifications flow again end-to-end.
        var refired = WaitForFire(node.Source.Watch(SettingsScope.Global));
        node.Source.Publish(SettingsScope.Global, UniqueKey());
        await refired.WaitAsync(FireTimeout, Ct);
    }

    /// <summary>Kills the LISTEN backend(s) server-side, simulating a dropped listen connection.</summary>
    private static async Task TerminateListenBackendsAsync(string connectionString, CancellationToken ct) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity " +
            "WHERE pid <> pg_backend_pid() AND query ILIKE 'LISTEN%'";
        await command.ExecuteNonQueryAsync(ct);
    }

    private static EfCoreSettingsStore<SettingsIntegrationDbContext> CreateStore(
        SettingsIntegrationDbContext context,
        PostgreSqlSettingsChangeOptions options) {
        return new EfCoreSettingsStore<SettingsIntegrationDbContext>(context,
            new PostgreSqlTransactionalSettingsChangeNotifier(options), TimeProvider.System);
    }

    private static Task WaitForFire(IChangeToken token) {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.RegisterChangeCallback(static state => ((TaskCompletionSource)state!).TrySetResult(), completion);
        return completion.Task;
    }

    /// <summary>One "node": a change source with its running listener over a dedicated data source.</summary>
    private sealed class ChangeNode : IAsyncDisposable {
        private readonly PostgreSqlSettingsChangeListener _listener;

        private ChangeNode(
            PostgreSqlSettingsChangeSource source,
            PostgreSqlSettingsChangeListener listener,
            PostgreSqlSettingsChangeOptions options) {
            Source = source;
            _listener = listener;
            Options = options;
        }

        public PostgreSqlSettingsChangeSource Source { get; }

        public PostgreSqlSettingsChangeOptions Options { get; }

        public static async Task<ChangeNode> StartAsync(string connectionString, CancellationToken cancellationToken) {
            var options = new PostgreSqlSettingsChangeOptions();
            var source = new PostgreSqlSettingsChangeSource(
                NpgsqlDataSource.Create(connectionString),
                true,
                options,
                NullLogger<PostgreSqlSettingsChangeSource>.Instance);
            var listener = new PostgreSqlSettingsChangeListener(
                source, options, NullLogger<PostgreSqlSettingsChangeListener>.Instance);

            await listener.StartAsync(cancellationToken);
            await listener.Listening.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return new ChangeNode(source, listener, options);
        }

        public async ValueTask DisposeAsync() {
            await _listener.StopAsync(CancellationToken.None);
            _listener.Dispose();
            await Source.DisposeAsync();
        }
    }
}
