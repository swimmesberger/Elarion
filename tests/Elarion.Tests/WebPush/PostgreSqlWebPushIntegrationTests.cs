using Elarion.WebPush.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Elarion.Tests.WebPush;

/// <summary>
/// Starts a disposable PostgreSQL container for the Web Push store integration tests and creates the schema
/// once. Skips (never fails) when Docker is unavailable, mirroring the device identity fixture.
/// </summary>
public sealed class PostgreSqlWebPushFixture : IAsyncLifetime, IWebPushStoreFixture<WebPushIntegrationDbContext> {
    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public string SkipReason { get; private set; } = "";

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync() {
        PostgreSqlContainer container;
        try {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) {
            SkipReason = $"PostgreSQL Testcontainer unavailable (Docker required): {ex.Message}";
            return;
        }

        _container = container;
        ConnectionString = container.GetConnectionString();
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        IsAvailable = true;
    }

    public async ValueTask DisposeAsync() {
        if (_container is not null) await _container.DisposeAsync();
    }

    public void Configure(DbContextOptionsBuilder options) {
        options.UseNpgsql(ConnectionString);
    }

    public WebPushIntegrationDbContext CreateContext() {
        var options = new DbContextOptionsBuilder<WebPushIntegrationDbContext>();
        Configure(options);
        return new WebPushIntegrationDbContext(options.Options);
    }
}

/// <summary>Context mapping the Web Push tables the way a generated context would (the seam calls <c>UseElarionWebPush</c>).</summary>
public sealed class WebPushIntegrationDbContext(DbContextOptions<WebPushIntegrationDbContext> options)
    : DbContext(options) {
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();

    public DbSet<VapidKeyEntity> VapidKeys => Set<VapidKeyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        modelBuilder.UseElarionWebPush();
    }
}

[Trait("Category", "Integration")]
public sealed class PostgreSqlWebPushIntegrationTests(PostgreSqlWebPushFixture fixture)
    : WebPushStoreTestBase<WebPushIntegrationDbContext>(fixture), IClassFixture<PostgreSqlWebPushFixture>;
