namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Declares that the annotated partial <c>DbContext</c> hosts the Elarion PostgreSQL blob tables. The Elarion
/// blob-storage source generator emits the <c>DbSet&lt;StoredBlob&gt;</c> property and applies the model
/// configuration (via <see cref="PostgreSqlBlobStorageModelBuilderExtensions.UseElarionBlobStorage"/>) — both the
/// metadata and the content table — through the EF generator's model-configuration seam, so the host writes
/// neither the DbSet nor the table mapping. Requires <c>[GenerateDbSets]</c> on the same context (mirrors
/// <c>[GenerateElarionIdempotencyKeys]</c>).
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionBlobStorage]
/// public sealed partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options) {
///     protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionBlobStorageAttribute : Attribute {
    /// <summary>Whether to use snake_case table/column names. Defaults to <c>true</c>.</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>
    /// The metadata table name, or <c>null</c> for the default (<c>stored_blobs</c> /
    /// <c>StoredBlobs</c> depending on <see cref="SnakeCase"/>).
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// The content table name, or <c>null</c> for the default (<c>blob_contents</c> /
    /// <c>BlobContents</c> depending on <see cref="SnakeCase"/>).
    /// </summary>
    public string? ContentTableName { get; set; }

    /// <summary>The schema, or <c>null</c> for the provider's default schema.</summary>
    public string? Schema { get; set; }
}
