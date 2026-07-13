using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Identity;

/// <summary>
/// Registration-shape tests for <c>AddElarionIdentity&lt;TUser, TRole, TDbContext&gt;</c> — the key type is
/// inferred from the user/role types by ASP.NET Identity itself, so the extension takes no key type parameter.
/// </summary>
public sealed class ElarionIdentityRegistrationTests {
    public sealed class IdentityTestDbContext(DbContextOptions<IdentityTestDbContext> options) : DbContext(options);

    [Fact]
    public void AddElarionIdentity_RegistersIdentityStoresAndCurrentUser() {
        var services = new ServiceCollection();

        var builder = services.AddElarionIdentity<ApplicationUser, ApplicationRole, IdentityTestDbContext>();

        builder.UserType.Should().Be<ApplicationUser>();
        builder.RoleType.Should().Be<ApplicationRole>();
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(UserManager<ApplicationUser>));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IUserStore<ApplicationUser>));
        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(ICurrentUser));
    }

    [Fact]
    public void AddElarionIdentity_HonorsOptedOutTokenProviders() {
        var withDefaults = new ServiceCollection();
        withDefaults.AddElarionIdentity<ApplicationUser, ApplicationRole, IdentityTestDbContext>();

        var withoutDefaults = new ServiceCollection();
        withoutDefaults.AddElarionIdentity<ApplicationUser, ApplicationRole, IdentityTestDbContext>(
            configure: options => options.AddDefaultTokenProviders = false);

        withDefaults.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(DataProtectorTokenProvider<ApplicationUser>));
        withoutDefaults.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(DataProtectorTokenProvider<ApplicationUser>));
    }
}
