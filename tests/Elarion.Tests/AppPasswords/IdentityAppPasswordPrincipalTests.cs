using System.Net;
using AwesomeAssertions;
using Elarion.AppPasswords;
using Elarion.AppPasswords.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary><c>AddElarionIdentityAppPasswordPrincipals&lt;TUser&gt;()</c>: the principal through ASP.NET Core Identity.</summary>
public sealed class IdentityAppPasswordPrincipalTests {
    [Fact]
    public async Task TheIdentityUser_IsSignedInWithItsClaims_AndALockedOutOrDeletedOneIsRefused() {
        var users = new TestUserStore();
        users.Add(new TestUser { Id = "u-1", UserName = "one@example.com" });
        users.Add(new TestUser { Id = "u-2", UserName = "two@example.com", LockoutEnd = DateTimeOffset.UtcNow.AddDays(1) });
        await using var host = await AppPasswordTestHost.StartAsync(
            services => {
                services.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore();
                services.AddIdentityCore<TestUser>();
                services.AddSingleton<IUserStore<TestUser>>(users);
                services.AddElarionIdentityAppPasswordPrincipals<TestUser>();
            },
            defaultPrincipals: false);
        var active = await host.CreateAsync("u-1", AppPasswordScope.Dav);
        var lockedOut = await host.CreateAsync("u-2", AppPasswordScope.Dav);
        var deleted = await host.CreateAsync("u-3", AppPasswordScope.Dav);

        var activeResponse = await host.GetAsync("/dav/whoami", "one@example.com", active.Secret);
        var lockedOutResponse = await host.GetAsync("/dav/whoami", "two@example.com", lockedOut.Secret);
        var deletedResponse = await host.GetAsync("/dav/whoami", "three@example.com", deleted.Secret);

        activeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await AppPasswordTestHost.BodyAsync(activeResponse)).Should()
            .Be($"u-1|{active.Password.Id}|dav|{IdentityConstants.ApplicationScheme}");
        lockedOutResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        deletedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheApplicationsCanSignInRule_RefusesADeactivatedAccountOnEveryRequest() {
        var users = new TestUserStore();
        var active = new TestUser { Id = "u-1", UserName = "one@example.com" };
        users.Add(active);
        await using var host = await AppPasswordTestHost.StartAsync(
            services => {
                services.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore();
                services.AddIdentityCore<TestUser>();
                services.AddSingleton<IUserStore<TestUser>>(users);
                services.AddElarionIdentityAppPasswordPrincipals<TestUser>((user, context) => {
                    context.Services.Should().NotBeNull();
                    return ValueTask.FromResult(user.IsActive);
                });
            },
            defaultPrincipals: false);
        var password = await host.CreateAsync("u-1", AppPasswordScope.Dav);

        var before = await host.GetAsync("/dav/whoami", "one@example.com", password.Secret);
        active.IsActive = false;
        var after = await host.GetAsync("/dav/whoami", "one@example.com", password.Secret);

        before.StatusCode.Should().Be(HttpStatusCode.OK);
        after.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ARegisteredSignInManager_RefusesAnAccountItWouldNotSignIn() {
        var users = new TestUserStore();
        users.Add(new TestUser { Id = "u-1", UserName = "one@example.com" });
        users.Add(new TestUser { Id = "u-2", UserName = "two@example.com", IsActive = false });
        await using var host = await AppPasswordTestHost.StartAsync(
            services => {
                services.AddElarionAppPasswords().AddElarionInMemoryAppPasswordStore();
                services.AddIdentityCore<TestUser>(o => o.SignIn.RequireConfirmedAccount = true).AddSignInManager();
                services.AddSingleton<IUserStore<TestUser>>(users);
                services.AddSingleton<IUserConfirmation<TestUser>, ActiveUserConfirmation>();
                services.AddElarionIdentityAppPasswordPrincipals<TestUser>();
            },
            defaultPrincipals: false);
        var confirmed = await host.CreateAsync("u-1", AppPasswordScope.Dav);
        var unconfirmed = await host.CreateAsync("u-2", AppPasswordScope.Dav);

        var confirmedResponse = await host.GetAsync("/dav/whoami", "one@example.com", confirmed.Secret);
        var unconfirmedResponse = await host.GetAsync("/dav/whoami", "two@example.com", unconfirmed.Secret);

        confirmedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        unconfirmedResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>Identity's account-confirmation seam, as an application's "may sign in" rule plugs into it.</summary>
    private sealed class ActiveUserConfirmation : IUserConfirmation<TestUser> {
        public Task<bool> IsConfirmedAsync(UserManager<TestUser> manager, TestUser user) => Task.FromResult(user.IsActive);
    }

    public sealed class TestUser {
        public required string Id { get; init; }

        public required string UserName { get; set; }

        public DateTimeOffset? LockoutEnd { get; set; }

        public bool IsActive { get; set; } = true;
    }

    /// <summary>The smallest user store with lockout: enough for FindByIdAsync, IsLockedOutAsync and the claims factory.</summary>
    public sealed class TestUserStore : IUserLockoutStore<TestUser> {
        private readonly Dictionary<string, TestUser> _users = new(StringComparer.Ordinal);

        public void Add(TestUser user) {
            _users[user.Id] = user;
        }

        public void Dispose() { }

        public Task<string> GetUserIdAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult(user.Id);
        }

        public Task<string?> GetUserNameAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult<string?>(user.UserName);
        }

        public Task SetUserNameAsync(TestUser user, string? userName, CancellationToken cancellationToken) {
            user.UserName = userName ?? "";
            return Task.CompletedTask;
        }

        public Task<string?> GetNormalizedUserNameAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult<string?>(user.UserName.ToUpperInvariant());
        }

        public Task SetNormalizedUserNameAsync(TestUser user, string? normalizedName, CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }

        public Task<IdentityResult> CreateAsync(TestUser user, CancellationToken cancellationToken) {
            Add(user);
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<IdentityResult> UpdateAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<IdentityResult> DeleteAsync(TestUser user, CancellationToken cancellationToken) {
            _users.Remove(user.Id);
            return Task.FromResult(IdentityResult.Success);
        }

        public Task<TestUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) {
            return Task.FromResult(_users.GetValueOrDefault(userId));
        }

        public Task<TestUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) {
            return Task.FromResult(_users.Values.FirstOrDefault(user =>
                string.Equals(user.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<DateTimeOffset?> GetLockoutEndDateAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult(user.LockoutEnd);
        }

        public Task SetLockoutEndDateAsync(TestUser user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken) {
            user.LockoutEnd = lockoutEnd;
            return Task.CompletedTask;
        }

        public Task<int> IncrementAccessFailedCountAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult(1);
        }

        public Task ResetAccessFailedCountAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }

        public Task<int> GetAccessFailedCountAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult(0);
        }

        public Task<bool> GetLockoutEnabledAsync(TestUser user, CancellationToken cancellationToken) {
            return Task.FromResult(true);
        }

        public Task SetLockoutEnabledAsync(TestUser user, bool enabled, CancellationToken cancellationToken) {
            return Task.CompletedTask;
        }
    }
}
