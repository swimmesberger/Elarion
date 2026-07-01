using Microsoft.EntityFrameworkCore;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Configures the EF Core model used by <see cref="PostgreSqlBlobStore{TDbContext}"/>.
/// </summary>
public static class PostgreSqlBlobStorageModelBuilderExtensions {
    /// <summary>
    /// Adds the blob metadata and content tables to the EF Core model.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UsePostgreSqlBlobStorage(this ModelBuilder modelBuilder) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<StoredBlob>(builder => {
            builder.ToTable("stored_blobs");
            builder.HasKey(e => e.Id);
            builder.HasIndex(e => new { e.Container, e.Name }).IsUnique();

            // Partial index over pending, expiring rows only: the garbage collector's "oldest expiry
            // first" scan stays a tiny indexed probe regardless of how many committed blobs exist.
            // State is stored as its int value, so Pending is 0. Filtered indexes are supported by
            // PostgreSQL (and SQL Server/SQLite); on a provider without them, drop the filter.
            builder.HasIndex(e => e.ExpiresAt)
                .HasFilter("state = 0 AND expires_at IS NOT NULL");

            builder.Property(e => e.Id)
                .HasColumnName("id")
                .ValueGeneratedNever();

            builder.Property(e => e.Container)
                .HasColumnName("container")
                .IsRequired();

            builder.Property(e => e.Name)
                .HasColumnName("name")
                .IsRequired();

            builder.Property(e => e.ContentType)
                .HasColumnName("content_type")
                .IsRequired();

            builder.Property(e => e.Size)
                .HasColumnName("size");

            builder.Property(e => e.CreatedAt)
                .HasColumnName("created_at");

            builder.Property(e => e.State)
                .HasColumnName("state")
                .HasConversion<int>();

            builder.Property(e => e.ExpiresAt)
                .HasColumnName("expires_at");

            builder.Property(e => e.OwnerId)
                .HasColumnName("owner_id");
        });

        modelBuilder.Entity<BlobContentRow>(builder => {
            builder.ToTable("blob_contents");
            builder.HasKey(e => e.BlobId);

            builder.Property(e => e.BlobId)
                .HasColumnName("blob_id")
                .ValueGeneratedNever();

            builder.Property(e => e.Data)
                .HasColumnName("data")
                .IsRequired();

            builder.HasOne<StoredBlob>()
                .WithMany()
                .HasForeignKey(e => e.BlobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        return modelBuilder;
    }
}
