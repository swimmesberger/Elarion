using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AppPasswords;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary>
/// <see cref="IAppPasswords"/> over the in-memory store: the "Manage app passwords" scenarios of spec 0006 that need
/// no HTTP, and the secret and hash contract.
/// </summary>
public sealed class AppPasswordsTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_ReturnsTheSecretOnce_AndTheListShowsNameCreationAndLastUseWithoutIt() {
        var (services, time, store) = Create();
        var appPasswords = services.GetRequiredService<IAppPasswords>();

        var issued = await appPasswords.CreateAsync("user-1", "iPhone", AppPasswordScope.Dav, Ct);

        issued.IsSuccess.Should().BeTrue();
        issued.Value.Secret.Should().StartWith("elap_");
        issued.Value.Secret.Length.Should().Be("elap_".Length + 43);
        issued.Value.Password.Name.Should().Be("iPhone");
        issued.Value.Password.CreatedAt.Should().Be(time.GetUtcNow());
        issued.Value.ToString().Should().NotContain(issued.Value.Secret);

        var stored = await store.FindByHashAsync(IndependentHash(issued.Value.Secret), Ct);
        stored.Should().NotBeNull("only the SHA-256 of the secret is stored, and it is the lookup key");

        time.Advance(TimeSpan.FromMinutes(1));
        (await appPasswords.VerifyAsync(issued.Value.Secret, AppPasswordScope.Dav, Ct)).Should().NotBeNull();

        var list = await appPasswords.ListAsync("user-1", Ct);
        list.Should().ContainSingle();
        list[0].Id.Should().Be(issued.Value.Password.Id);
        list[0].Name.Should().Be("iPhone");
        list[0].Scope.Should().Be(AppPasswordScope.Dav);
        list[0].CreatedAt.Should().Be(issued.Value.Password.CreatedAt);
        list[0].LastUsedAt.Should().Be(time.GetUtcNow());
        typeof(AppPasswordInfo).GetProperties().Select(property => property.Name).Should().NotContain(["Secret", "Hash"]);
    }

    [Fact]
    public async Task Secrets_Hold256RandomBitsInBase64Url_AndDiffer() {
        var (services, _, _) = Create(options => options.SecretPrefix = "");
        var appPasswords = services.GetRequiredService<IAppPasswords>();

        var first = (await appPasswords.CreateAsync("user-1", "a", AppPasswordScope.Dav, Ct)).Value.Secret;
        var second = (await appPasswords.CreateAsync("user-1", "b", AppPasswordScope.Dav, Ct)).Value.Secret;

        first.Should().NotBe(second);
        first.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        Convert.FromBase64String(first.Replace('-', '+').Replace('_', '/') + "=").Should().HaveCount(32);
    }

    [Fact]
    public void ComputeHash_IsSha256OverUtf8AsStandardBase64() {
        AppPasswordSecrets.ComputeHash("abc").Should().Be("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=");
        AppPasswordSecrets.ComputeHash("legacy_äöü:pass").Should().Be(IndependentHash("legacy_äöü:pass"));
    }

    [Fact]
    public async Task Revoke_DeletesThePassword_AndTheNextVerificationFails() {
        var (services, _, _) = Create();
        var appPasswords = services.GetRequiredService<IAppPasswords>();
        var issued = (await appPasswords.CreateAsync("user-1", "iPhone", AppPasswordScope.Dav, Ct)).Value;

        var revoked = await appPasswords.RevokeAsync("user-1", issued.Password.Id, Ct);

        revoked.IsSuccess.Should().BeTrue();
        (await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct)).Should().BeNull();
        (await appPasswords.ListAsync("user-1", Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Revoke_AForeignOrUnknownPassword_IsNotFound() {
        var (services, _, _) = Create();
        var appPasswords = services.GetRequiredService<IAppPasswords>();
        var issued = (await appPasswords.CreateAsync("user-1", "iPhone", AppPasswordScope.Dav, Ct)).Value;

        var foreign = await appPasswords.RevokeAsync("user-2", issued.Password.Id, Ct);
        var unknown = await appPasswords.RevokeAsync("user-1", Guid.CreateVersion7(), Ct);

        foreign.IsSuccess.Should().BeFalse();
        foreign.Error.Code.Should().Be(AppPasswordErrorCodes.NotFound);
        foreign.Error.Kind.Should().Be(ErrorKind.NotFound);
        unknown.Error.Code.Should().Be(AppPasswordErrorCodes.NotFound);
        (await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Create_BeyondTheMaximumPerUser_IsRefusedWithACodedError() {
        var (services, _, _) = Create(options => options.MaxPerUser = 2);
        var appPasswords = services.GetRequiredService<IAppPasswords>();
        await appPasswords.CreateAsync("user-1", "a", AppPasswordScope.Dav, Ct);
        await appPasswords.CreateAsync("user-1", "b", AppPasswordScope.CalendarFeed("shifts", "u-1"), Ct);

        var third = await appPasswords.CreateAsync("user-1", "c", AppPasswordScope.Dav, Ct);
        var otherUser = await appPasswords.CreateAsync("user-2", "a", AppPasswordScope.Dav, Ct);

        third.IsSuccess.Should().BeFalse();
        third.Error.Code.Should().Be(AppPasswordErrorCodes.LimitReached);
        third.Error.Kind.Should().Be(ErrorKind.Conflict);
        third.Error.Data.Should().Be(new AppPasswordLimitReachedData { MaxPerUser = 2 }, "an application words the message from it");
        otherUser.IsSuccess.Should().BeTrue();
        (await appPasswords.ListAsync("user-1", Ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Verify_RefusesAnotherScope() {
        var (services, _, _) = Create();
        var appPasswords = services.GetRequiredService<IAppPasswords>();
        var feedScope = AppPasswordScope.CalendarFeed("shifts", "u-1");
        var dav = (await appPasswords.CreateAsync("user-1", "iPhone", AppPasswordScope.Dav, Ct)).Value;
        var feed = (await appPasswords.CreateAsync("user-1", "Outlook", feedScope, Ct)).Value;

        (await appPasswords.VerifyAsync(dav.Secret, feedScope, Ct)).Should().BeNull();
        (await appPasswords.VerifyAsync(dav.Secret, AppPasswordScopeKind.CalendarFeed, Ct)).Should().BeNull();
        (await appPasswords.VerifyAsync(feed.Secret, AppPasswordScope.Dav, Ct)).Should().BeNull();
        (await appPasswords.VerifyAsync(feed.Secret, AppPasswordScope.CalendarFeed("shifts", "u-2"), Ct)).Should().BeNull();

        var match = await appPasswords.VerifyAsync(feed.Secret, AppPasswordScopeKind.CalendarFeed, Ct);
        match.Should().NotBeNull();
        match!.Scope.Should().Be(feedScope);
        match.UserId.Should().Be("user-1");
        match.Id.Should().Be(feed.Password.Id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("elap_unknown")]
    public async Task Verify_RefusesAnEmptyOrUnknownSecret(string secret) {
        var (services, _, _) = Create();

        var match = await services.GetRequiredService<IAppPasswords>().VerifyAsync(secret, AppPasswordScope.Dav, Ct);

        match.Should().BeNull();
    }

    [Fact]
    public async Task Verify_RecordsTheLastUseAtMostOncePerInterval() {
        var (services, time, store) = Create(options => options.LastUseInterval = TimeSpan.FromMinutes(15));
        var appPasswords = services.GetRequiredService<IAppPasswords>();
        var issued = (await appPasswords.CreateAsync("user-1", "iPhone", AppPasswordScope.Dav, Ct)).Value;

        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);
        var firstUse = time.GetUtcNow();
        time.Advance(TimeSpan.FromMinutes(10));
        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);
        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);

        store.Touches.Should().Be(1);
        (await appPasswords.ListAsync("user-1", Ct))[0].LastUsedAt.Should().Be(firstUse);

        time.Advance(TimeSpan.FromMinutes(6));
        await appPasswords.VerifyAsync(issued.Secret, AppPasswordScope.Dav, Ct);

        store.Touches.Should().Be(2);
        (await appPasswords.ListAsync("user-1", Ct))[0].LastUsedAt.Should().Be(time.GetUtcNow());
    }

    [Fact]
    public async Task Verify_AcceptsARowCopiedFromAnotherStoreWithALegacyPrefix() {
        var (services, time, store) = Create(options => options.SecretPrefix = "new_");
        const string legacySecret = "old_6xQ2vXk8JqzRyF0aGd1sPpLbWcNmTe3u";
        await store.TryAddAsync(new AppPasswordRecord {
            Id = Guid.CreateVersion7(),
            UserId = "42",
            Name = "Thunderbird",
            Scope = AppPasswordScope.Dav,
            Hash = IndependentHash(legacySecret),
            CreatedAt = time.GetUtcNow(),
        }, 10, Ct);

        var match = await services.GetRequiredService<IAppPasswords>().VerifyAsync(legacySecret, AppPasswordScope.Dav, Ct);

        match.Should().NotBeNull();
        match!.UserId.Should().Be("42");
    }

    [Fact]
    public async Task Create_RefusesAnEmptyOrOverlongName() {
        var (services, _, _) = Create();
        var appPasswords = services.GetRequiredService<IAppPasswords>();

        var empty = async () => await appPasswords.CreateAsync("user-1", " ", AppPasswordScope.Dav, Ct);
        var overlong = async () => await appPasswords.CreateAsync(
            "user-1", new string('x', AppPasswordOptions.MaxNameLength + 1), AppPasswordScope.Dav, Ct);

        await empty.Should().ThrowAsync<ArgumentException>();
        await overlong.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Create_RefusesAUserIdLongerThanTheStoredColumn() {
        var (services, _, _) = Create();
        var appPasswords = services.GetRequiredService<IAppPasswords>();

        var overlong = async () => await appPasswords.CreateAsync(
            new string('u', AppPasswordOptions.MaxUserIdLength + 1), "a", AppPasswordScope.Dav, Ct);
        var longest = await appPasswords.CreateAsync(new string('u', AppPasswordOptions.MaxUserIdLength), "a", AppPasswordScope.Dav, Ct);

        await overlong.Should().ThrowAsync<ArgumentException>();
        longest.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Options_BindFromTheSection() {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["AppPasswords:SecretPrefix"] = "myapp_",
            ["AppPasswords:MaxPerUser"] = "20",
            ["AppPasswords:LastUseInterval"] = "00:05:00",
        }).Build();
        var services = new ServiceCollection()
            .AddElarionAppPasswords(configuration.GetSection(AppPasswordOptions.SectionName))
            .BuildServiceProvider();

        var options = services.GetRequiredService<IOptions<AppPasswordOptions>>().Value;

        options.SecretPrefix.Should().Be("myapp_");
        options.MaxPerUser.Should().Be(20);
        options.LastUseInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("bad:prefix", 10)]
    [InlineData("elap_", 0)]
    public void Options_OutOfRange_AreRefused(string prefix, int maxPerUser) {
        var services = new ServiceCollection()
            .AddElarionAppPasswords(configure: options => {
                options.SecretPrefix = prefix;
                options.MaxPerUser = maxPerUser;
            })
            .BuildServiceProvider();

        var read = () => services.GetRequiredService<IOptions<AppPasswordOptions>>().Value;

        read.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Resolving_WithoutAStore_NamesTheRegistrations() {
        using var services = new ServiceCollection().AddElarionAppPasswords().BuildServiceProvider();
        using var scope = services.CreateScope();

        var resolve = () => scope.ServiceProvider.GetRequiredService<IAppPasswords>();

        resolve.Should().Throw<InvalidOperationException>()
            .WithMessage("*AddElarionAppPasswordsEntityFrameworkCore*AddElarionInMemoryAppPasswordStore*");
    }

    internal static string IndependentHash(string secret) {
        using var sha = SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(new UTF8Encoding(false).GetBytes(secret)));
    }

    private static (IServiceProvider Services, FakeTimeProvider Time, CountingStore Store) Create(
        Action<AppPasswordOptions>? configure = null) {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, 9, 0, 0, TimeSpan.Zero));
        var store = new CountingStore();
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(time)
            .AddElarionAppPasswords(configure: configure)
            .AddSingleton<IAppPasswordStore>(store)
            .BuildServiceProvider();
        return (services.CreateScope().ServiceProvider, time, store);
    }

    /// <summary>The in-memory store, counting the last-use writes that reach it.</summary>
    internal sealed class CountingStore : IAppPasswordStore {
        private readonly InMemoryAppPasswordStore _inner = new();

        public int Touches { get; private set; }

        public ValueTask<bool> TryAddAsync(AppPasswordRecord record, int maxPerUser,
            CancellationToken cancellationToken = default) {
            return _inner.TryAddAsync(record, maxPerUser, cancellationToken);
        }

        public ValueTask<IReadOnlyList<AppPasswordInfo>> ListAsync(string userId,
            CancellationToken cancellationToken = default) {
            return _inner.ListAsync(userId, cancellationToken);
        }

        public ValueTask<bool> RemoveAsync(string userId, Guid id, CancellationToken cancellationToken = default) {
            return _inner.RemoveAsync(userId, id, cancellationToken);
        }

        public ValueTask<AppPasswordRecord?> FindByHashAsync(string hash, CancellationToken cancellationToken = default) {
            return _inner.FindByHashAsync(hash, cancellationToken);
        }

        public ValueTask TouchAsync(Guid id, DateTimeOffset usedAt, DateTimeOffset staleBefore,
            CancellationToken cancellationToken = default) {
            Touches++;
            return _inner.TouchAsync(id, usedAt, staleBefore, cancellationToken);
        }
    }
}
