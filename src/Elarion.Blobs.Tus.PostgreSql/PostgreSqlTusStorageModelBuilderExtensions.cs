using Microsoft.EntityFrameworkCore;

namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Configures the EF Core model used by <see cref="PostgreSqlTusUploadStore{TDbContext}"/>.
/// </summary>
public static class PostgreSqlTusStorageModelBuilderExtensions {
    /// <summary>
    /// Adds the tus upload staging table to the EF Core model. Call from <c>OnModelCreating</c> on the
    /// context that backs the tus store — or annotate the context with <c>[GenerateElarionTusStorage]</c>
    /// and let the bundled generator call this for you.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The table name, or <see langword="null"/> for the default (<c>tus_uploads</c> /
    /// <c>TusUploads</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column names. Defaults to <see langword="true"/>.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UseElarionTusStorage(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "tus_uploads" : "TusUploads");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);

        modelBuilder.Entity<TusUploadRow>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(e => e.Id);

            // Index over the expiry instant so the garbage collector's "oldest expiry first" scan stays an
            // indexed probe. It is not filtered on incompleteness: a completed session also carries an
            // expiry (its completed-retention window), and the collector reaps both kinds by ExpiresAt, so
            // completed rows never grow the staging table unbounded.
            builder.HasIndex(e => e.ExpiresAt);

            builder.Property(e => e.Id).HasColumnName(snakeCase ? "id" : "Id").ValueGeneratedNever();
            builder.Property(e => e.Container).HasColumnName(snakeCase ? "container" : "Container").IsRequired();
            builder.Property(e => e.Name).HasColumnName(snakeCase ? "name" : "Name").IsRequired();
            builder.Property(e => e.UploadLength).HasColumnName(snakeCase ? "upload_length" : "UploadLength");
            builder.Property(e => e.UploadOffset).HasColumnName(snakeCase ? "upload_offset" : "UploadOffset");
            builder.Property(e => e.ContentType).HasColumnName(snakeCase ? "content_type" : "ContentType").IsRequired();
            builder.Property(e => e.Metadata).HasColumnName(snakeCase ? "metadata" : "Metadata");
            builder.Property(e => e.OwnerId).HasColumnName(snakeCase ? "owner_id" : "OwnerId");
            builder.Property(e => e.ExpiresAt).HasColumnName(snakeCase ? "expires_at" : "ExpiresAt");
            builder.Property(e => e.CreatedAt).HasColumnName(snakeCase ? "created_at" : "CreatedAt");
            builder.Property(e => e.BlobId).HasColumnName(snakeCase ? "blob_id" : "BlobId");
            builder.Property(e => e.Data).HasColumnName(snakeCase ? "data" : "Data").IsRequired();
        });

        return modelBuilder;
    }
}
