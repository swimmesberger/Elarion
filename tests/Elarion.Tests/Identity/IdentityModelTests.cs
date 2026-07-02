using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Elarion.Tests.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>;

public sealed class ApplicationRole : IdentityRole<Guid>;

public sealed class IdentityModelTests {
    private static IModel BuildModel(bool snakeCase) {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.ApplyElarionIdentity<ApplicationUser, ApplicationRole, Guid>(snakeCase: snakeCase);
        return modelBuilder.FinalizeModel();
    }

    [Fact]
    public void MapsAllIdentityEntitiesWithKeysSoStoresWorkWithoutIdentityDbContext() {
        var model = BuildModel(snakeCase: true);

        model.FindEntityType(typeof(ApplicationUser)).Should().NotBeNull();
        model.FindEntityType(typeof(ApplicationRole)).Should().NotBeNull();
        model.FindEntityType(typeof(IdentityUserClaim<Guid>)).Should().NotBeNull();
        model.FindEntityType(typeof(IdentityRoleClaim<Guid>)).Should().NotBeNull();
        model.FindEntityType(typeof(IdentityUserLogin<Guid>)).Should().NotBeNull();
        model.FindEntityType(typeof(IdentityUserToken<Guid>)).Should().NotBeNull();

        // Composite keys are the part EF conventions cannot infer.
        model.FindEntityType(typeof(IdentityUserRole<Guid>))!.FindPrimaryKey()!.Properties
            .Select(property => property.Name).Should().Equal("UserId", "RoleId");
        model.FindEntityType(typeof(IdentityUserLogin<Guid>))!.FindPrimaryKey()!.Properties
            .Select(property => property.Name).Should().Equal("LoginProvider", "ProviderKey");
        model.FindEntityType(typeof(IdentityUserToken<Guid>))!.FindPrimaryKey()!.Properties
            .Select(property => property.Name).Should().Equal("UserId", "LoginProvider", "Name");
    }

    [Fact]
    public void SnakeCaseSetsTableColumnAndIndexNames() {
        var model = BuildModel(snakeCase: true);

        model.FindEntityType(typeof(ApplicationUser))!.GetTableName().Should().Be("users");
        model.FindEntityType(typeof(ApplicationRole))!.GetTableName().Should().Be("roles");
        model.FindEntityType(typeof(IdentityUserClaim<Guid>))!.GetTableName().Should().Be("user_claims");
        model.FindEntityType(typeof(IdentityUserRole<Guid>))!.GetTableName().Should().Be("user_roles");
        model.FindEntityType(typeof(IdentityUserLogin<Guid>))!.GetTableName().Should().Be("user_logins");
        model.FindEntityType(typeof(IdentityRoleClaim<Guid>))!.GetTableName().Should().Be("role_claims");
        model.FindEntityType(typeof(IdentityUserToken<Guid>))!.GetTableName().Should().Be("user_tokens");

        var user = model.FindEntityType(typeof(ApplicationUser))!;
        user.FindProperty(nameof(IdentityUser<Guid>.NormalizedUserName))!.GetColumnName().Should().Be("normalized_user_name");
        user.FindProperty(nameof(IdentityUser<Guid>.AccessFailedCount))!.GetColumnName().Should().Be("access_failed_count");

        user.GetIndexes()
            .Single(index => index.Properties.Any(property => property.Name == nameof(IdentityUser<Guid>.NormalizedUserName)))
            .GetDatabaseName().Should().Be("ix_users_normalized_user_name");
        user.GetIndexes()
            .Single(index => index.Properties.Any(property => property.Name == nameof(IdentityUser<Guid>.NormalizedEmail)))
            .GetDatabaseName().Should().Be("ix_users_normalized_email");
        model.FindEntityType(typeof(ApplicationRole))!.GetIndexes()
            .Single(index => index.Properties.Any(property => property.Name == nameof(IdentityRole<Guid>.NormalizedName)))
            .GetDatabaseName().Should().Be("ix_roles_normalized_name");
    }

    [Fact]
    public void SchemaAndTablePrefixAreApplied() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.ApplyElarionIdentity<ApplicationUser, ApplicationRole, Guid>(schema: "auth", tablePrefix: "app_");
        var model = modelBuilder.FinalizeModel();

        var user = model.FindEntityType(typeof(ApplicationUser))!;
        user.GetTableName().Should().Be("app_users");
        user.GetSchema().Should().Be("auth");
        model.FindEntityType(typeof(IdentityUserToken<Guid>))!.GetTableName().Should().Be("app_user_tokens");

        // Index names incorporate the prefixed table name so two prefixed Identity models never collide.
        user.GetIndexes()
            .Single(index => index.Properties.Any(property => property.Name == nameof(IdentityUser<Guid>.NormalizedUserName)))
            .GetDatabaseName().Should().Be("ix_app_users_normalized_user_name");
    }

    [Fact]
    public void NonSnakeCaseKeepsAspNetDefaults() {
        var model = BuildModel(snakeCase: false);

        model.FindEntityType(typeof(ApplicationUser))!.GetTableName().Should().Be("AspNetUsers");
        model.FindEntityType(typeof(ApplicationRole))!.GetTableName().Should().Be("AspNetRoles");
        model.FindEntityType(typeof(ApplicationUser))!
            .FindProperty(nameof(IdentityUser<Guid>.NormalizedUserName))!
            .GetColumnName().Should().Be("NormalizedUserName");
    }
}
