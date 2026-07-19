using Microsoft.EntityFrameworkCore;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>Registers the Elarion audit-log table on a <see cref="ModelBuilder"/>.</summary>
public static class AuditingModelBuilderExtensions {
    /// <summary>
    /// Maps <see cref="AuditLogEntry"/> to the <c>elarion_audit_log</c> table (by default) with the indexes the
    /// classic audit searches need: by resource, by parent resource, by user, and by time — each time-suffixed so
    /// filtered views page with keyset pagination off the index. Called for you by the
    /// <c>[GenerateElarionAuditing]</c> generator through the EF model-configuration seam; call it by hand in
    /// <c>OnModelCreating</c> otherwise.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The table name, or <see langword="null"/> for the default (<c>elarion_audit_log</c> /
    /// <c>ElarionAuditLog</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column/index names. Defaults to <see langword="true"/>.</param>
    public static ModelBuilder UseElarionAuditing(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "elarion_audit_log" : "ElarionAuditLog");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<AuditLogEntry>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(entry => entry.Id);
            // The id is client-assigned (Guid v7) so the insert can ride the caller's transaction without a
            // round-trip; ADR-0038's convention pass would do this too, but the mapping states it explicitly.
            builder.Property(entry => entry.Id).HasColumnName(snakeCase ? "id" : "Id").ValueGeneratedNever();

            builder.Property(entry => entry.OccurredAtUtc)
                .HasColumnName(snakeCase ? "occurred_at_utc" : "OccurredAtUtc");
            builder.Property(entry => entry.Action).HasColumnName(snakeCase ? "action" : "Action").HasMaxLength(256);
            builder.Property(entry => entry.Module).HasColumnName(snakeCase ? "module" : "Module").HasMaxLength(128);
            builder.Property(entry => entry.UserId).HasColumnName(snakeCase ? "user_id" : "UserId").HasMaxLength(128);
            builder.Property(entry => entry.ResourceType).HasColumnName(snakeCase ? "resource_type" : "ResourceType")
                .HasMaxLength(128);
            builder.Property(entry => entry.ResourceId).HasColumnName(snakeCase ? "resource_id" : "ResourceId")
                .HasMaxLength(256);
            builder.Property(entry => entry.ParentResourceType)
                .HasColumnName(snakeCase ? "parent_resource_type" : "ParentResourceType").HasMaxLength(128);
            builder.Property(entry => entry.ParentResourceId)
                .HasColumnName(snakeCase ? "parent_resource_id" : "ParentResourceId").HasMaxLength(256);
            builder.Property(entry => entry.Outcome).HasColumnName(snakeCase ? "outcome" : "Outcome").HasMaxLength(16);
            builder.Property(entry => entry.ErrorKind).HasColumnName(snakeCase ? "error_kind" : "ErrorKind")
                .HasMaxLength(32);
            builder.Property(entry => entry.CorrelationId).HasColumnName(snakeCase ? "correlation_id" : "CorrelationId")
                .HasMaxLength(64);
            builder.Property(entry => entry.Changes).HasColumnName(snakeCase ? "changes" : "Changes");
            builder.Property(entry => entry.Details).HasColumnName(snakeCase ? "details" : "Details");

            builder.HasIndex(entry => new { entry.ResourceType, entry.ResourceId, entry.OccurredAtUtc })
                .HasDatabaseName(snakeCase ? $"ix_{table}_resource" : $"IX_{table}_Resource");
            builder.HasIndex(entry => new { entry.ParentResourceType, entry.ParentResourceId, entry.OccurredAtUtc })
                .HasDatabaseName(snakeCase ? $"ix_{table}_parent" : $"IX_{table}_Parent");
            builder.HasIndex(entry => new { entry.UserId, entry.OccurredAtUtc })
                .HasDatabaseName(snakeCase ? $"ix_{table}_user" : $"IX_{table}_User");
            builder.HasIndex(entry => entry.OccurredAtUtc)
                .HasDatabaseName(snakeCase ? $"ix_{table}_occurred" : $"IX_{table}_Occurred");
        });

        return modelBuilder;
    }
}
