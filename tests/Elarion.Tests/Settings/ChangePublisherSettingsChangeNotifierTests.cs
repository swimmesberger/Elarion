using System.Transactions;
using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.Settings;

/// <summary>
/// Commit-gating of the default in-process notifier under a System.Transactions ambient transaction
/// (<c>TransactionScope</c>): EF exposes no <c>DbTransaction</c> there, so the write is not yet durable even
/// though <c>Database.CurrentTransaction</c> is null — the notification must ride the ambient transaction's
/// completion (announced on commit, dropped on rollback) instead of firing immediately. No database needed:
/// the notifier only inspects the context's transaction state.
/// </summary>
public sealed class ChangePublisherSettingsChangeNotifierTests {
    private sealed class RecordingPublisher : ISettingsChangePublisher {
        public List<(SettingsScope Scope, string Key)> Published { get; } = [];

        public void Publish(SettingsScope scope, string key) => Published.Add((scope, key));
    }

    private sealed class NotifierTestDbContext(DbContextOptions<NotifierTestDbContext> options)
        : DbContext(options);

    /// <summary>A context configured for Npgsql but never opened — CurrentTransaction stays null.</summary>
    private static NotifierTestDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<NotifierTestDbContext>()
            .UseNpgsql("Host=localhost;Database=elarion;Username=elarion;Password=elarion")
            .Options);

    private static (ChangePublisherSettingsChangeNotifier Notifier, RecordingPublisher Publisher) CreateNotifier() {
        var publisher = new RecordingPublisher();
        var dispatch = new SettingsChangeDispatchScope(publisher, NullLogger<SettingsChangeDispatchScope>.Instance);
        return (new ChangePublisherSettingsChangeNotifier(dispatch), publisher);
    }

    [Fact]
    public async Task AmbientTransactionScope_AnnouncedOnlyAfterCommit() {
        var (notifier, publisher) = CreateNotifier();
        await using var context = CreateContext();

        using (var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled)) {
            await notifier.NotifyAsync(context, SettingsScope.Global, "app:title", CancellationToken.None);

            // Pre-commit: announcing now would let a watcher observe a value that is not yet durable.
            publisher.Published.Should().BeEmpty();
            transaction.Complete();
        }

        publisher.Published.Should().ContainSingle().Which.Key.Should().Be("app:title");
    }

    [Fact]
    public async Task AmbientTransactionScope_DroppedOnRollback() {
        var (notifier, publisher) = CreateNotifier();
        await using var context = CreateContext();

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled)) {
            await notifier.NotifyAsync(context, SettingsScope.Global, "app:title", CancellationToken.None);
            // Disposed without Complete(): the ambient transaction aborts.
        }

        publisher.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task NoAmbientTransaction_AnnouncedImmediately() {
        var (notifier, publisher) = CreateNotifier();
        await using var context = CreateContext();

        await notifier.NotifyAsync(context, SettingsScope.Global, "app:title", CancellationToken.None);

        publisher.Published.Should().ContainSingle().Which.Key.Should().Be("app:title");
    }
}
