using System.Security.Claims;
using AwesomeAssertions;
using Elarion.AspNetCore.Identity;
using Elarion.AspNetCore.ProxyIdentity;
using Elarion.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Elarion.Tests.ProxyIdentity;

/// <summary>
/// The documented recipe for an application that replaces the proxy's principal with one built by ASP.NET Core
/// Identity: the first <c>ICurrentUser</c> mapping registered stays in effect, and pointing
/// <see cref="IdentityOptions.ClaimsIdentity"/> at it makes the replacement principal readable through it.
/// </summary>
public sealed class ReplacedPrincipalTests {
    [Fact]
    public void FirstRegisteredMapping_StaysInEffect() {
        var services = new ServiceCollection();
        services.AddElarionProxyIdentity(EmptyConfiguration(), new DevelopmentEnvironment());
        services.AddElarionCurrentUser(currentUser => currentUser.UserIdClaimType = ClaimTypes.NameIdentifier);
        using var provider = services.BuildServiceProvider();

        var mapping = provider.GetRequiredService<ClaimsCurrentUserOptions>();

        mapping.UserIdClaimType.Should().Be("sub");
        mapping.EmailClaimType.Should().Be("email");
        mapping.RoleClaimType.Should().Be("roles");
    }

    [Fact]
    public async Task IdentityPrincipal_BuiltUnderTheMapping_IsReadByTheCurrentUser() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionProxyIdentity(EmptyConfiguration(), new DevelopmentEnvironment());
        services.AddIdentityCore<LocalAccount>().AddUserStore<LocalAccountStore>();
        services.AddOptions<IdentityOptions>().Configure<ClaimsCurrentUserOptions>((identity, currentUser) => {
            identity.ClaimsIdentity.UserIdClaimType = currentUser.UserIdClaimType;
            identity.ClaimsIdentity.EmailClaimType = currentUser.EmailClaimType;
            identity.ClaimsIdentity.RoleClaimType = currentUser.RoleClaimType;
        });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<LocalAccount>>()
            .CreateAsync(new LocalAccount("account-1", "person@example.com"));
        var currentUser = scope.ServiceProvider.GetRequiredService<ClaimsPrincipalCurrentUser>();
        currentUser.Initialize(principal);

        currentUser.IsAuthenticated.Should().BeTrue();
        currentUser.UserId.Should().Be("account-1");
        currentUser.Email.Should().Be("person@example.com");
    }

    private static IConfiguration EmptyConfiguration() {
        return new ConfigurationBuilder().Build();
    }

    private sealed record LocalAccount(string Id, string Email);

    /// <summary>The smallest store the claims-principal factory reads: id, user name and e-mail address.</summary>
    private sealed class LocalAccountStore : IUserEmailStore<LocalAccount> {
        public Task<string> GetUserIdAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(user.Id);
        public Task<string?> GetUserNameAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);
        public Task<string?> GetEmailAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.Email);
        public Task<bool> GetEmailConfirmedAsync(LocalAccount user, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<string?> GetNormalizedEmailAsync(LocalAccount user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(LocalAccount user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetUserNameAsync(LocalAccount user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(LocalAccount user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEmailAsync(LocalAccount user, string? email, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEmailConfirmedAsync(LocalAccount user, bool confirmed, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetNormalizedEmailAsync(LocalAccount user, string? normalizedEmail, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalAccount?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalAccount?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocalAccount?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(LocalAccount user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(LocalAccount user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(LocalAccount user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
    }

    private sealed class DevelopmentEnvironment : IHostEnvironment {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
