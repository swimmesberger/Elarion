using Microsoft.EntityFrameworkCore;

namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>Registers the Elarion resource-grants table on a <see cref="ModelBuilder"/>.</summary>
public static class ResourceGrantsModelBuilderExtensions {
    /// <summary>
    /// Maps <see cref="ResourceGrantEntity"/> to the <c>elarion_resource_grants</c> table with the composite key
    /// <c>(resource_type, resource_id, principal_kind, principal_id, operation)</c> and a secondary index on the
    /// principal. Called for you by the <c>[GenerateElarionResourceGrants]</c> generator through the EF
    /// model-configuration seam; call it by hand in <c>OnModelCreating</c> if you do not use that attribute
    /// (alongside, for example, <c>UseElarionSettings()</c>).
    /// </summary>
    public static ModelBuilder ApplyElarionResourceGrants(this ModelBuilder modelBuilder, bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<ResourceGrantEntity>(builder => {
            builder.ToTable(snakeCase ? "elarion_resource_grants" : "ResourceGrants");
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
                .HasDatabaseName(snakeCase ? "ix_elarion_resource_grants_principal" : "IX_ResourceGrants_Principal");
        });

        return modelBuilder;
    }
}
