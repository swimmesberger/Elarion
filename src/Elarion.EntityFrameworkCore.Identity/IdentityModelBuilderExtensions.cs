using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Elarion.EntityFrameworkCore.Identity;

/// <summary>
/// Applies the ASP.NET Core Identity entity model to a <b>plain</b> <see cref="DbContext"/> — the keys,
/// composite keys, unique normalized indexes, relationships, and max-lengths that
/// <c>IdentityDbContext&lt;TUser, TRole, TKey&gt;</c> would configure — so the application context does not
/// have to inherit <c>IdentityDbContext</c>. <c>AddEntityFrameworkStores</c> resolves the Identity entities
/// via <c>context.Set&lt;T&gt;()</c>, so mapping them is the only requirement.
/// </summary>
/// <remarks>
/// This is the primitive the generated <c>[GenerateElarionIdentity]</c> code calls (with the attribute's
/// types baked in); advanced hosts and tests may call it directly. The configuration mirrors the pinned
/// ASP.NET Core Identity version. With <paramref name="snakeCase"/> it sets snake_case table, column, and
/// index names directly — fully self-contained, with no <c>EFCore.NamingConventions</c> dependency.
/// </remarks>
public static class IdentityModelBuilderExtensions {
    /// <summary>Applies the Identity entity model to <paramref name="modelBuilder"/>.</summary>
    /// <param name="modelBuilder">The model builder, typically from <c>OnModelCreating</c>.</param>
    /// <param name="snakeCase">
    /// When <see langword="true"/> (default), uses snake_case table/column/index names (<c>users</c>,
    /// <c>normalized_user_name</c>, …); when <see langword="false"/>, the ASP.NET <c>AspNet*</c> defaults.
    /// </param>
    public static ModelBuilder ApplyElarionIdentity<TUser, TRole, TKey>(
        this ModelBuilder modelBuilder, bool snakeCase = true)
        where TUser : IdentityUser<TKey>
        where TRole : IdentityRole<TKey>
        where TKey : IEquatable<TKey> {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var userTable = snakeCase ? "users" : "AspNetUsers";
        var roleTable = snakeCase ? "roles" : "AspNetRoles";
        var userClaimsTable = snakeCase ? "user_claims" : "AspNetUserClaims";
        var userRolesTable = snakeCase ? "user_roles" : "AspNetUserRoles";
        var userLoginsTable = snakeCase ? "user_logins" : "AspNetUserLogins";
        var roleClaimsTable = snakeCase ? "role_claims" : "AspNetRoleClaims";
        var userTokensTable = snakeCase ? "user_tokens" : "AspNetUserTokens";

        var userNameIndex = snakeCase ? "ix_users_normalized_user_name" : "UserNameIndex";
        var emailIndex = snakeCase ? "ix_users_normalized_email" : "EmailIndex";
        var roleNameIndex = snakeCase ? "ix_roles_normalized_name" : "RoleNameIndex";

        modelBuilder.Entity<TUser>(b => {
            b.HasKey(user => user.Id);
            b.HasIndex(user => user.NormalizedUserName).HasDatabaseName(userNameIndex).IsUnique();
            b.HasIndex(user => user.NormalizedEmail).HasDatabaseName(emailIndex);
            b.ToTable(userTable);
            b.Property(user => user.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(user => user.UserName).HasMaxLength(256);
            b.Property(user => user.NormalizedUserName).HasMaxLength(256);
            b.Property(user => user.Email).HasMaxLength(256);
            b.Property(user => user.NormalizedEmail).HasMaxLength(256);

            b.HasMany<IdentityUserClaim<TKey>>().WithOne().HasForeignKey(claim => claim.UserId).IsRequired();
            b.HasMany<IdentityUserLogin<TKey>>().WithOne().HasForeignKey(login => login.UserId).IsRequired();
            b.HasMany<IdentityUserToken<TKey>>().WithOne().HasForeignKey(token => token.UserId).IsRequired();
            b.HasMany<IdentityUserRole<TKey>>().WithOne().HasForeignKey(userRole => userRole.UserId).IsRequired();

            ApplyColumns(b, snakeCase, UserColumns);
        });

        modelBuilder.Entity<IdentityUserClaim<TKey>>(b => {
            b.HasKey(claim => claim.Id);
            b.ToTable(userClaimsTable);
            ApplyColumns(b, snakeCase, UserClaimColumns);
        });

        modelBuilder.Entity<IdentityUserLogin<TKey>>(b => {
            b.HasKey(login => new { login.LoginProvider, login.ProviderKey });
            b.ToTable(userLoginsTable);
            ApplyColumns(b, snakeCase, UserLoginColumns);
        });

        modelBuilder.Entity<IdentityUserToken<TKey>>(b => {
            b.HasKey(token => new { token.UserId, token.LoginProvider, token.Name });
            b.ToTable(userTokensTable);
            ApplyColumns(b, snakeCase, UserTokenColumns);
        });

        modelBuilder.Entity<TRole>(b => {
            b.HasKey(role => role.Id);
            b.HasIndex(role => role.NormalizedName).HasDatabaseName(roleNameIndex).IsUnique();
            b.ToTable(roleTable);
            b.Property(role => role.ConcurrencyStamp).IsConcurrencyToken();
            b.Property(role => role.Name).HasMaxLength(256);
            b.Property(role => role.NormalizedName).HasMaxLength(256);

            b.HasMany<IdentityUserRole<TKey>>().WithOne().HasForeignKey(userRole => userRole.RoleId).IsRequired();
            b.HasMany<IdentityRoleClaim<TKey>>().WithOne().HasForeignKey(claim => claim.RoleId).IsRequired();

            ApplyColumns(b, snakeCase, RoleColumns);
        });

        modelBuilder.Entity<IdentityRoleClaim<TKey>>(b => {
            b.HasKey(claim => claim.Id);
            b.ToTable(roleClaimsTable);
            ApplyColumns(b, snakeCase, RoleClaimColumns);
        });

        modelBuilder.Entity<IdentityUserRole<TKey>>(b => {
            b.HasKey(userRole => new { userRole.UserId, userRole.RoleId });
            b.ToTable(userRolesTable);
            ApplyColumns(b, snakeCase, UserRoleColumns);
        });

        return modelBuilder;
    }

    // Explicit snake_case column names so the mapping is self-contained (no naming-convention dependency).
    // Each value equals what EFCore.NamingConventions' snake_case convention would produce, so pairing this
    // with an app that uses that convention yields an identical schema (no migration diff).
    private static void ApplyColumns(
        EntityTypeBuilder builder, bool snakeCase, (string Property, string Column)[] columns) {
        if (!snakeCase) {
            return;
        }

        foreach (var (property, column) in columns) {
            builder.Property(property).HasColumnName(column);
        }
    }

    private static readonly (string Property, string Column)[] UserColumns = [
        ("Id", "id"),
        ("UserName", "user_name"),
        ("NormalizedUserName", "normalized_user_name"),
        ("Email", "email"),
        ("NormalizedEmail", "normalized_email"),
        ("EmailConfirmed", "email_confirmed"),
        ("PasswordHash", "password_hash"),
        ("SecurityStamp", "security_stamp"),
        ("ConcurrencyStamp", "concurrency_stamp"),
        ("PhoneNumber", "phone_number"),
        ("PhoneNumberConfirmed", "phone_number_confirmed"),
        ("TwoFactorEnabled", "two_factor_enabled"),
        ("LockoutEnd", "lockout_end"),
        ("LockoutEnabled", "lockout_enabled"),
        ("AccessFailedCount", "access_failed_count"),
    ];

    private static readonly (string Property, string Column)[] RoleColumns = [
        ("Id", "id"),
        ("Name", "name"),
        ("NormalizedName", "normalized_name"),
        ("ConcurrencyStamp", "concurrency_stamp"),
    ];

    private static readonly (string Property, string Column)[] UserClaimColumns = [
        ("Id", "id"),
        ("UserId", "user_id"),
        ("ClaimType", "claim_type"),
        ("ClaimValue", "claim_value"),
    ];

    private static readonly (string Property, string Column)[] RoleClaimColumns = [
        ("Id", "id"),
        ("RoleId", "role_id"),
        ("ClaimType", "claim_type"),
        ("ClaimValue", "claim_value"),
    ];

    private static readonly (string Property, string Column)[] UserRoleColumns = [
        ("UserId", "user_id"),
        ("RoleId", "role_id"),
    ];

    private static readonly (string Property, string Column)[] UserLoginColumns = [
        ("LoginProvider", "login_provider"),
        ("ProviderKey", "provider_key"),
        ("ProviderDisplayName", "provider_display_name"),
        ("UserId", "user_id"),
    ];

    private static readonly (string Property, string Column)[] UserTokenColumns = [
        ("UserId", "user_id"),
        ("LoginProvider", "login_provider"),
        ("Name", "name"),
        ("Value", "value"),
    ];
}
