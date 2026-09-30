using AwesomeAssertions;
using Elarion.WebPush;
using Elarion.WebPush.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.WebPush;

/// <summary>
/// File-based SQLite fixture for the Web Push store tests. SQLite runs in-process (no Docker), so these tests
/// always run; pooling is off so each context's connection closes for real instead of holding the file.
/// </summary>
public sealed class SqliteWebPushFixture : IAsyncLifetime, IWebPushStoreFixture<ConvertingWebPushDbContext> {
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "elarion_sqlite_webpush_" + Guid.CreateVersion7().ToString("N") + ".db");

    public string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = _path, Pooling = false }.ConnectionString;

    public bool IsAvailable => true;

    public string SkipReason => "";

    public async ValueTask InitializeAsync() {
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public ValueTask DisposeAsync() {
        try {
            File.Delete(_path);
        }
        catch (IOException) {
            // Best-effort temp cleanup; the OS reclaims it regardless.
        }

        return ValueTask.CompletedTask;
    }

    public void Configure(DbContextOptionsBuilder options) {
        options.UseSqlite(ConnectionString);
    }

    public ConvertingWebPushDbContext CreateContext() {
        var options = new DbContextOptionsBuilder<ConvertingWebPushDbContext>();
        Configure(options);
        return new ConvertingWebPushDbContext(options.Options);
    }
}

/// <summary>
/// A context with a model-wide value converter, the way SQLite applications commonly store
/// <see cref="DateTimeOffset"/> (as a sortable integer, since SQLite cannot order the default text form). The
/// stores' raw statements must write through it exactly as a tracked insert would.
/// </summary>
public sealed class ConvertingWebPushDbContext(DbContextOptions<ConvertingWebPushDbContext> options) : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionWebPush();
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}

public sealed class SqliteWebPushIntegrationTests(SqliteWebPushFixture fixture)
    : WebPushStoreTestBase<ConvertingWebPushDbContext>(fixture), IClassFixture<SqliteWebPushFixture> {
    [Fact]
    public async Task SubscriptionStore_Upsert_WritesThroughTheModelsValueConverters() {
        await using var provider = CreateProvider();
        var user = NewId();
        using var subscriber = new TestPushSubscriber(NewEndpoint());
        var seen = new DateTimeOffset(2026, 9, 30, 19, 0, 0, TimeSpan.Zero);

        await provider.GetRequiredService<IPushSubscriptionStore>()
            .UpsertAsync(subscriber.ToSubscription(user) with { CreatedAt = seen, LastSeenAt = seen }, TestToken);

        // What the column literally holds: the converter's integer, not the provider's default text.
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync(TestToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT typeof(last_seen_on_utc), last_seen_on_utc FROM elarion_push_subscriptions WHERE user_id = $user";
        command.Parameters.AddWithValue("$user", user);
        await using var reader = await command.ExecuteReaderAsync(TestToken);
        (await reader.ReadAsync(TestToken)).Should().BeTrue();
        reader.GetString(0).Should().Be("integer");
        reader.GetInt64(1).Should().Be(new DateTimeOffsetToBinaryConverter().ConvertToProvider(seen) as long?);
    }
}
