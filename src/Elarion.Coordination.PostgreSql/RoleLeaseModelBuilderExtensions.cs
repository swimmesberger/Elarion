using Microsoft.EntityFrameworkCore;

namespace Elarion.Coordination.PostgreSql;

/// <summary>
/// Maps the <see cref="RoleLeaseEntity"/> onto a model. Normally applied through the
/// <see cref="GenerateElarionRoleLeasesAttribute"/> seam; call it directly from
/// <c>OnModelCreating</c> when the context is hand-written.
/// </summary>
public static class RoleLeaseModelBuilderExtensions {
    /// <summary>Adds the role lease table to the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="tableName">Overrides the table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="schema">Optional schema.</param>
    /// <param name="snakeCase">Whether table/column names default to snake_case (the Elarion default).</param>
    public static ModelBuilder UseElarionRoleLeases(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var table = tableName ?? (snakeCase ? "elarion_role_leases" : "ElarionRoleLeases");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<RoleLeaseEntity>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(entity => entity.Role);
            builder.Property(entity => entity.Role)
                .HasColumnName(snakeCase ? "role" : "Role")
                .HasMaxLength(RoleLeaseOptions.MaximumRoleNameLength);
            builder.Property(entity => entity.Owner)
                .HasColumnName(snakeCase ? "owner" : "Owner")
                .HasMaxLength(256);
            builder.Property(entity => entity.Address)
                .HasColumnName(snakeCase ? "address" : "Address")
                .HasMaxLength(512);
            builder.Property(entity => entity.ExpiresOnUtc)
                .HasColumnName(snakeCase ? "expires_on_utc" : "ExpiresOnUtc");
        });
        return modelBuilder;
    }
}
