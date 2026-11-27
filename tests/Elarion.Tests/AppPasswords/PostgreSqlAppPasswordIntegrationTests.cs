using System.Net;
using AwesomeAssertions;
using Elarion.AppPasswords;
using Elarion.AppPasswords.AspNetCore;
using Elarion.AppPasswords.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary>The EF app-password store on real PostgreSQL, and both adoption paths' data contract.</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlAppPasswordIntegrationTests(PostgreSqlAppPasswordFixture fixture)
    : IClassFixture<PostgreSqlAppPasswordFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateListVerifyRevoke_RoundTrips() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var provider = CreateProvider();
        var userId = NewUserId();
        var appPasswords = provider.CreateScope().ServiceProvider.GetRequiredService<IAppPasswords>();

        var dav = (await appPasswords.CreateAsync(userId, "iPhone", AppPasswordScope.Dav, Ct)).Value;
        var feed = (await appPasswords.CreateAsync(userId, "Outlook", AppPasswordScope.CalendarFeed("shifts", "u-1"), Ct)).Value;

        var list = await appPasswords.ListAsync(userId, Ct);
        list.Select(password => password.Name).Should().Equal("iPhone", "Outlook");
        list[1].Scope.Should().Be(AppPasswordScope.CalendarFeed("shifts", "u-1"));
        list[0].LastUsedAt.Should().BeNull();

        var match = await appPasswords.VerifyAsync(dav.Secret, AppPasswordScope.Dav, Ct);
        match.Should().NotBeNull();
        match!.UserId.Should().Be(userId);
        (await appPasswords.VerifyAsync(feed.Secret, AppPasswordScopeKind.CalendarFeed, Ct))!.Scope.CalendarKey
            .Should().Be("u-1");
        (await appPasswords.ListAsync(userId, Ct))[0].LastUsedAt.Should().NotBeNull();

        (await appPasswords.RevokeAsync(NewUserId(), dav.Password.Id, Ct)).Error.Code
            .Should().Be(AppPasswordErrorCodes.NotFound);
        (await appPasswords.RevokeAsync(userId, dav.Password.Id, Ct)).IsSuccess.Should().BeTrue();
        (await appPasswords.VerifyAsync(dav.Secret, AppPasswordScope.Dav, Ct)).Should().BeNull();
        (await appPasswords.ListAsync(userId, Ct)).Should().ContainSingle();
    }

    [Fact]
    public async Task TheTable_HasThePublishedNamesAndAUniqueHashIndex() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var context = fixture.CreateContext();

        var columns = await context.Database
            .SqlQueryRaw<string>(
                "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'elarion_app_passwords' ORDER BY column_name")
            .ToListAsync(Ct);
        var uniqueIndexes = await context.Database
            .SqlQueryRaw<string>(
                "SELECT indexdef AS \"Value\" FROM pg_indexes WHERE tablename = 'elarion_app_passwords' AND indexdef LIKE 'CREATE UNIQUE INDEX%'")
            .ToListAsync(Ct);

        columns.Should().Equal("created_at", "hash", "id", "last_used_at", "name", "scope", "user_id");
        uniqueIndexes.Should().Contain(definition => definition.Contains("(hash)"));
    }

    [Fact]
    public async Task ConcurrentCreations_NeverPassTheCap() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        await using var nodeA = CreateProvider(options => options.MaxPerUser = 3);
        await using var nodeB = CreateProvider(options => options.MaxPerUser = 3);
        var userId = NewUserId();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async index => {
            var node = index % 2 == 0 ? nodeA : nodeB;
            await using var scope = node.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IAppPasswords>()
                .CreateAsync(userId, $"device {index}", AppPasswordScope.Dav, Ct);
        }));

        results.Count(result => result.IsSuccess).Should().Be(3);
        results.Where(result => !result.IsSuccess).Should()
            .OnlyContain(result => result.Error.Code == AppPasswordErrorCodes.LimitReached);
        var appPasswords = nodeA.CreateScope().ServiceProvider.GetRequiredService<IAppPasswords>();
        (await appPasswords.ListAsync(userId, Ct)).Should().HaveCount(3);
    }

    [Fact]
    public async Task TheLastUse_IsWrittenAtMostOncePerInterval() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
        await using var provider = CreateProvider(time: time);
        var appPasswords = provider.CreateScope().ServiceProvider.GetRequiredService<IAppPasswords>();
        var userId = NewUserId();
        var issued = (await appPasswords.CreateAsync(userId, "iPhone", AppPasswordScope.Dav, Ct)).Value;

        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromMinutes(14));
        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);
        (await appPasswords.ListAsync(userId, Ct))[0].LastUsedAt.Should().Be(first);

        time.Advance(TimeSpan.FromMinutes(2));
        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);
        (await appPasswords.ListAsync(userId, Ct))[0].LastUsedAt.Should().Be(time.GetUtcNow());

        // The store's own condition holds when a node with a stale read writes anyway.
        var store = provider.GetRequiredService<IAppPasswordStore>();
        await store.TouchAsync(issued.Password.Id, time.GetUtcNow().AddMinutes(1), time.GetUtcNow().AddMinutes(-15), Ct);
        (await appPasswords.ListAsync(userId, Ct))[0].LastUsedAt.Should().Be(time.GetUtcNow());
    }

    [Theory]
    [InlineData("elap_")]
    [InlineData("legacy_")]
    public async Task Adoption_ARowCopiedWithItsHash_VerifiesThroughTheDefaultCredentialCheck(string secretPrefix) {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var userId = NewUserId();
        var secret = secretPrefix + "Zm9vYmFyYmF6cXV4cXV1eGNvcmdlZ3JhdWx0Z2FycGx5";
        var id = Guid.CreateVersion7();
        await using (var context = fixture.CreateContext()) {
            // The migration step of an adopting application: plain SQL, the old row's hash unchanged.
            await context.Database.ExecuteSqlRawAsync(
                "INSERT INTO elarion_app_passwords (id, user_id, name, scope, hash, created_at, last_used_at) "
                + "VALUES ({0}, {1}, 'Thunderbird', 'dav', {2}, now() - interval '400 days', NULL)",
                [id, userId, AppPasswordsTests.IndependentHash(secret)], Ct);
        }

        await using var host = await AppPasswordTestHost.StartAsync(services => {
            services.AddDbContext<AppPasswordIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
            services.AddElarionAppPasswordsEntityFrameworkCore<AppPasswordIntegrationDbContext>(
                configure: options => options.SecretPrefix = "new_");
        });

        var response = await host.GetAsync("/dav/whoami", "someone@example.com", secret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppPasswordTestHost.BodyAsync(response)).Should().Be($"{userId}|{id}|dav|Test");
        var issued = await host.CreateAsync(userId, AppPasswordScope.Dav, "New phone");
        issued.Secret.Should().StartWith("new_");
    }

    private static string NewUserId() {
        return Guid.CreateVersion7().ToString();
    }

    private ServiceProvider CreateProvider(Action<AppPasswordOptions>? configure = null, TimeProvider? time = null) {
        var services = new ServiceCollection();
        if (time is not null) services.AddSingleton(time);
        services.AddDbContext<AppPasswordIntegrationDbContext>(options => options.UseNpgsql(fixture.ConnectionString));
        services.AddElarionAppPasswordsEntityFrameworkCore<AppPasswordIntegrationDbContext>(configure: configure);
        return services.BuildServiceProvider();
    }
}
