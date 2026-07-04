namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Declares that the annotated partial <c>DbContext</c> hosts the Elarion staged-upload table. The Elarion
/// staged-uploads source generator emits the <c>DbSet&lt;StagedUploadRow&gt;</c> property and applies the
/// model configuration (via <see cref="PostgreSqlStagedUploadModelBuilderExtensions.UseElarionStagedUploads"/>)
/// through the EF generator's model-configuration seam — so the host writes neither the DbSet nor the table
/// mapping. Requires <c>[GenerateDbSets]</c> on the same context (mirrors <c>[GenerateElarionBlobStorage]</c>,
/// which maps the blob tables a completed upload is written to).
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionBlobStorage]
/// [GenerateElarionStagedUploads]
/// public sealed partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options) {
///     protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionStagedUploadsAttribute : Attribute {
    /// <summary>Whether to use snake_case table/column names. Defaults to <c>true</c>.</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>
    /// The table name, or <c>null</c> for the default (<c>staged_uploads</c> /
    /// <c>StagedUploads</c> depending on <see cref="SnakeCase"/>).
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>The schema, or <c>null</c> for the provider's default schema.</summary>
    public string? Schema { get; set; }
}
