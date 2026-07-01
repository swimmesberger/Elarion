using Microsoft.EntityFrameworkCore;

namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Configures the EF Core model used by <see cref="PostgreSqlTusUploadStore{TDbContext}"/>.
/// </summary>
public static class PostgreSqlTusStorageModelBuilderExtensions {
    /// <summary>
    /// Adds the tus upload staging table to the EF Core model. Call from <c>OnModelCreating</c> on the
    /// context that backs the tus store.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UseElarionTusStorage(this ModelBuilder modelBuilder) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<TusUploadRow>(builder => {
            builder.ToTable("tus_uploads");
            builder.HasKey(e => e.Id);

            // Partial index over incomplete sessions only: the garbage collector's "oldest expiry first"
            // scan stays a tiny indexed probe regardless of how many completed sessions linger.
            builder.HasIndex(e => e.ExpiresAt)
                .HasFilter("blob_id IS NULL");

            builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            builder.Property(e => e.Container).HasColumnName("container").IsRequired();
            builder.Property(e => e.Name).HasColumnName("name").IsRequired();
            builder.Property(e => e.UploadLength).HasColumnName("upload_length");
            builder.Property(e => e.UploadOffset).HasColumnName("upload_offset");
            builder.Property(e => e.ContentType).HasColumnName("content_type").IsRequired();
            builder.Property(e => e.Metadata).HasColumnName("metadata");
            builder.Property(e => e.OwnerId).HasColumnName("owner_id");
            builder.Property(e => e.ExpiresAt).HasColumnName("expires_at");
            builder.Property(e => e.CreatedAt).HasColumnName("created_at");
            builder.Property(e => e.BlobId).HasColumnName("blob_id");
            builder.Property(e => e.Data).HasColumnName("data").IsRequired();
        });

        return modelBuilder;
    }
}
