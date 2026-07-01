using Microsoft.EntityFrameworkCore;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Configures the EF Core model used by <see cref="PostgreSqlBlobStore{TDbContext}"/>.
/// </summary>
public static class PostgreSqlBlobStorageModelBuilderExtensions {
    /// <summary>
    /// Adds the blob metadata and content tables to the EF Core model. Call from <c>OnModelCreating</c> — or
    /// annotate the context with <c>[GenerateElarionBlobStorage]</c> and let the bundled generator call this for you.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <param name="tableName">
    /// The metadata table name, or <see langword="null"/> for the default (<c>stored_blobs</c> /
    /// <c>StoredBlobs</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="contentTableName">
    /// The content table name, or <see langword="null"/> for the default (<c>blob_contents</c> /
    /// <c>BlobContents</c> depending on <paramref name="snakeCase"/>).
    /// </param>
    /// <param name="schema">The schema, or <see langword="null"/> to use the provider's default schema.</param>
    /// <param name="snakeCase">Whether to use snake_case table/column names. Defaults to <see langword="true"/>.</param>
    /// <returns>The same model builder for chaining.</returns>
    public static ModelBuilder UseElarionBlobStorage(
        this ModelBuilder modelBuilder,
        string? tableName = null,
        string? contentTableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var table = tableName ?? (snakeCase ? "stored_blobs" : "StoredBlobs");
        var contentTable = contentTableName ?? (snakeCase ? "blob_contents" : "BlobContents");
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentTable);

        modelBuilder.Entity<StoredBlob>(builder => {
            builder.ToTable(table, schema);
            builder.HasKey(e => e.Id);
            builder.HasIndex(e => new { e.Container, e.Name }).IsUnique();

            // Partial index over pending, expiring rows only: the garbage collector's "oldest expiry
            // first" scan stays a tiny indexed probe regardless of how many committed blobs exist.
            // State is stored as its int value, so Pending is 0. Filtered indexes are supported by
            // PostgreSQL (and SQL Server/SQLite); on a provider without them, drop the filter. The
            // PascalCase filter quotes the identifiers because unquoted identifiers fold to lower case
            // on PostgreSQL.
            builder.HasIndex(e => e.ExpiresAt)
                .HasFilter(snakeCase
                    ? "state = 0 AND expires_at IS NOT NULL"
                    : "\"State\" = 0 AND \"ExpiresAt\" IS NOT NULL");

            builder.Property(e => e.Id)
                .HasColumnName(snakeCase ? "id" : "Id")
                .ValueGeneratedNever();

            builder.Property(e => e.Container)
                .HasColumnName(snakeCase ? "container" : "Container")
                .IsRequired();

            builder.Property(e => e.Name)
                .HasColumnName(snakeCase ? "name" : "Name")
                .IsRequired();

            builder.Property(e => e.ContentType)
                .HasColumnName(snakeCase ? "content_type" : "ContentType")
                .IsRequired();

            builder.Property(e => e.Size)
                .HasColumnName(snakeCase ? "size" : "Size");

            builder.Property(e => e.CreatedAt)
                .HasColumnName(snakeCase ? "created_at" : "CreatedAt");

            builder.Property(e => e.State)
                .HasColumnName(snakeCase ? "state" : "State")
                .HasConversion<int>();

            builder.Property(e => e.ExpiresAt)
                .HasColumnName(snakeCase ? "expires_at" : "ExpiresAt");

            builder.Property(e => e.OwnerId)
                .HasColumnName(snakeCase ? "owner_id" : "OwnerId");
        });

        modelBuilder.Entity<BlobContentRow>(builder => {
            builder.ToTable(contentTable, schema);
            builder.HasKey(e => e.BlobId);

            builder.Property(e => e.BlobId)
                .HasColumnName(snakeCase ? "blob_id" : "BlobId")
                .ValueGeneratedNever();

            builder.Property(e => e.Data)
                .HasColumnName(snakeCase ? "data" : "Data")
                .IsRequired();

            builder.HasOne<StoredBlob>()
                .WithMany()
                .HasForeignKey(e => e.BlobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        return modelBuilder;
    }
}
