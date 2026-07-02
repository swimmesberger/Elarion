using Microsoft.EntityFrameworkCore;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>Registers the Elarion resource-grants table on a <see cref="ModelBuilder"/>.</summary>
public static class ResourceGrantsModelBuilderExtensions {
    /// <summary>
    /// Maps <see cref="ResourceGrantEntity"/> to the <c>elarion_resource_grants</c> table (by default) with the
    /// composite key <c>(resource_type, resource_id, principal_kind, principal_id, operation)</c> and a secondary
    /// index on the principal. Called for you by the <c>[GenerateElarionResourceGrants]</c> generator through the
    /// EF model-configuration seam; call it by hand in <c>OnModelCreating</c> if you do not use that attribute
    /// (alongside, for example, <c>UseElarionSettings()</c>).
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The table name, or <see langword="null"/> for the default (<c>elarion_resource_grants</c> /
    /// <c>ElarionResourceGrants</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column/index names. Defaults to <see langword="true"/>.</param>
    public static ModelBuilder ApplyElarionResourceGrants(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "elarion_resource_grants" : "ElarionResourceGrants");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<ResourceGrantEntity>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(grant => new
            {
                grant.ResourceType,
                grant.ResourceId,
                grant.PrincipalKind,
                grant.PrincipalId,
                grant.Operation,
            });

            builder.Property(grant => grant.ResourceType).HasColumnName(snakeCase ? "resource_type" : "ResourceType").HasMaxLength(128);
            builder.Property(grant => grant.ResourceId).HasColumnName(snakeCase ? "resource_id" : "ResourceId").HasMaxLength(256);
            builder.Property(grant => grant.PrincipalKind).HasColumnName(snakeCase ? "principal_kind" : "PrincipalKind").HasMaxLength(32);
            builder.Property(grant => grant.PrincipalId).HasColumnName(snakeCase ? "principal_id" : "PrincipalId").HasMaxLength(256);
            builder.Property(grant => grant.Operation).HasColumnName(snakeCase ? "operation" : "Operation").HasMaxLength(64);

            // Supports "what is this principal granted" lookups and revocation.
            builder.HasIndex(grant => new { grant.PrincipalKind, grant.PrincipalId })
                .HasDatabaseName(snakeCase ? $"ix_{table}_principal" : $"IX_{table}_Principal");
        });

        return modelBuilder;
    }
}
