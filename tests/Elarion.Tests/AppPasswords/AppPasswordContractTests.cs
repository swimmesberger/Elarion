using AwesomeAssertions;
using Elarion.AppPasswords;
using Elarion.AppPasswords.AspNetCore;
using Elarion.AppPasswords.EntityFrameworkCore;
using Elarion.WebDav.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Elarion.Tests.AppPasswords;

/// <summary>Contracts between the app-password packages and their neighbours that no compiler checks.</summary>
public sealed class AppPasswordContractTests {
    // The DAV host names the scheme by a string of its own, because the packages do not reference each other.
    [Fact]
    public void TheDavHostsDefaultScheme_IsTheAppPasswordScheme() {
        WebDavHostOptions.AppPasswordScheme.Should().Be(AppPasswordAuthenticationDefaults.AuthenticationScheme);
        new WebDavHostOptions().AuthenticationSchemes.Should().Equal(AppPasswordAuthenticationDefaults.AuthenticationScheme);
    }

    [Fact]
    public void TheStoredUserId_IsBoundedLikeIdentityKeys() {
        using var context = new AppPasswordIntegrationDbContext(new DbContextOptionsBuilder<AppPasswordIntegrationDbContext>()
            .UseNpgsql("Host=localhost;Database=unused").Options);

        var userId = context.Model.FindEntityType(typeof(AppPasswordEntity))!.FindProperty(nameof(AppPasswordEntity.UserId))!;

        userId.GetMaxLength().Should().Be(AppPasswordOptions.MaxUserIdLength).And.Be(256);
    }
}
