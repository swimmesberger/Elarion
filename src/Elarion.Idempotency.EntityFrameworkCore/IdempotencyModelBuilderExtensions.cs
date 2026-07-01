using Microsoft.EntityFrameworkCore;

namespace Elarion.Idempotency.EntityFrameworkCore;

/// <summary>Registers the Elarion idempotency-keys table on a <see cref="ModelBuilder"/>.</summary>
public static class IdempotencyModelBuilderExtensions {
    /// <summary>
    /// Maps <see cref="IdempotencyKeyEntity"/> to the <c>elarion_idempotency_keys</c> table with the composite
    /// unique key <c>(scope, owner, key)</c>, snake_case columns, a <c>version</c> concurrency token, and a
    /// secondary index over completed rows to keep the retention purge an indexed probe. Called for you by the
    /// <c>[GenerateElarionIdempotencyKeys]</c> generator through the EF model-configuration seam; call it by hand
    /// in <c>OnModelCreating</c> otherwise (alongside, for example, <c>UseElarionOutbox()</c>).
    /// </summary>
    public static ModelBuilder ApplyElarionIdempotencyKeys(this ModelBuilder modelBuilder, bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<IdempotencyKeyEntity>(builder => {
            builder.ToTable(snakeCase ? "elarion_idempotency_keys" : "ElarionIdempotencyKeys");
            builder.HasKey(entity => new { entity.Scope, entity.Owner, entity.Key });

            builder.Property(entity => entity.Scope).HasColumnName(snakeCase ? "scope" : "Scope").HasMaxLength(32);
            builder.Property(entity => entity.Owner).HasColumnName(snakeCase ? "owner" : "Owner").HasMaxLength(128);
            builder.Property(entity => entity.Key).HasColumnName(snakeCase ? "key" : "Key").HasMaxLength(256);
            builder.Property(entity => entity.Fingerprint).HasColumnName(snakeCase ? "fingerprint" : "Fingerprint").HasMaxLength(128);
            builder.Property(entity => entity.Completed).HasColumnName(snakeCase ? "completed" : "Completed");
            builder.Property(entity => entity.IsFailure).HasColumnName(snakeCase ? "is_failure" : "IsFailure");
            builder.Property(entity => entity.Payload).HasColumnName(snakeCase ? "payload" : "Payload");
            builder.Property(entity => entity.CreatedOnUtc).HasColumnName(snakeCase ? "created_on_utc" : "CreatedOnUtc");
            builder.Property(entity => entity.CompletedOnUtc).HasColumnName(snakeCase ? "completed_on_utc" : "CompletedOnUtc");
            builder.Property(entity => entity.ExpiresOnUtc).HasColumnName(snakeCase ? "expires_on_utc" : "ExpiresOnUtc");

            builder.Property(entity => entity.Version).HasColumnName(snakeCase ? "version" : "Version").IsConcurrencyToken();

            builder.HasIndex(entity => new { entity.Completed, entity.ExpiresOnUtc })
                .HasDatabaseName(snakeCase ? "ix_elarion_idempotency_keys_purge" : "IX_ElarionIdempotencyKeys_Purge");
        });

        return modelBuilder;
    }
}
