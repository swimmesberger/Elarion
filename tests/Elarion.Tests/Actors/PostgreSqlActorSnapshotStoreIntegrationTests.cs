using AwesomeAssertions;
using Elarion.Actors;
using Elarion.Actors.PostgreSql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Actors;

[Trait("Category", "Integration")]
public sealed class PostgreSqlActorSnapshotStoreIntegrationTests(PostgreSqlActorSnapshotStoreFixture fixture)
    : IClassFixture<PostgreSqlActorSnapshotStoreFixture> {
    private static CancellationToken TestToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_Read_Roundtrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();

        var etag = await store.WriteAsync(key, """{"balance":5}""", expectedETag: null, TestToken);
        etag.Should().Be("1");

        var snapshot = await store.ReadAsync(key, TestToken);
        snapshot.Should().NotBeNull();
        snapshot!.ETag.Should().Be("1");
        snapshot.Payload.Should().Contain("\"balance\"");
    }

    [Fact]
    public async Task Read_Missing_ReturnsNull() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();

        (await store.ReadAsync(NewKey(), TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Create_WhenSnapshotAlreadyExists_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();
        await store.WriteAsync(key, """{"balance":1}""", expectedETag: null, TestToken);

        var act = async () => await store.WriteAsync(key, """{"balance":2}""", expectedETag: null, TestToken);
        await act.Should().ThrowAsync<ActorSnapshotConcurrencyException>();

        // The ON CONFLICT create shape never poisons the connection; the row is untouched.
        (await store.ReadAsync(key, TestToken))!.Payload.Should().Contain("1");
    }

    [Fact]
    public async Task Replace_WithCurrentETag_IncrementsVersion() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();
        var etag = await store.WriteAsync(key, """{"balance":1}""", expectedETag: null, TestToken);

        etag = await store.WriteAsync(key, """{"balance":2}""", etag, TestToken);
        etag.Should().Be("2");

        var snapshot = await store.ReadAsync(key, TestToken);
        snapshot!.ETag.Should().Be("2");
        snapshot.Payload.Should().Contain("2");
    }

    [Fact]
    public async Task Replace_WithStaleETag_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();
        var etag = await store.WriteAsync(key, """{"balance":1}""", expectedETag: null, TestToken);
        await store.WriteAsync(key, """{"balance":2}""", etag, TestToken);

        var act = async () => await store.WriteAsync(key, """{"balance":3}""", etag, TestToken);
        (await act.Should().ThrowAsync<ActorSnapshotConcurrencyException>())
            .Which.ExpectedETag.Should().Be(etag);
    }

    [Fact]
    public async Task Clear_WithCurrentETag_DeletesTheRow() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();
        var etag = await store.WriteAsync(key, """{"balance":1}""", expectedETag: null, TestToken);

        await store.ClearAsync(key, etag, TestToken);

        (await store.ReadAsync(key, TestToken)).Should().BeNull();
    }

    [Fact]
    public async Task Clear_WithStaleETag_Throws() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();
        var etag = await store.WriteAsync(key, """{"balance":1}""", expectedETag: null, TestToken);
        await store.WriteAsync(key, """{"balance":2}""", etag, TestToken);

        var act = async () => await store.ClearAsync(key, etag, TestToken);
        await act.Should().ThrowAsync<ActorSnapshotConcurrencyException>();
    }

    [Fact]
    public async Task Payload_IsStoredAsQueryableJsonb() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<IActorSnapshotStore>();
        var key = NewKey();
        await store.WriteAsync(key, """{"stage":"shipped","attempts":3}""", expectedETag: null, TestToken);

        // The ops story: actor state is inspectable with plain SQL over the jsonb column.
        await using var context = fixture.CreateContext();
        var stage = await context.Database
            .SqlQuery<string>(
                $"SELECT state->>'stage' AS \"Value\" FROM elarion_actor_snapshots WHERE actor_name = {key.ActorName} AND actor_key = {key.Key}")
            .SingleAsync(TestToken);
        stage.Should().Be("shipped");
    }

    private static ActorSnapshotKey NewKey() =>
        new("Vault", Guid.CreateVersion7().ToString());

    private ServiceProvider CreateProvider() {
        var services = new ServiceCollection();
        services.AddDbContext<ActorSnapshotIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionPostgreSqlActorSnapshots<ActorSnapshotIntegrationDbContext>();
        return services.BuildServiceProvider();
    }
}
