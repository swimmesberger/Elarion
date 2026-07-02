namespace Elarion.Authorization.EntityFrameworkCore;

/// <summary>
/// Declares that the annotated partial <c>DbContext</c> hosts the Elarion resource-grants table. The Elarion
/// resource-grants source generator emits the <c>DbSet&lt;ResourceGrantEntity&gt;</c> property and applies the
/// grants model configuration (via <see cref="ResourceGrantsModelBuilderExtensions.ApplyElarionResourceGrants"/>)
/// through the EF generator's model-configuration seam — so the host writes neither the DbSet nor the table
/// mapping. Requires <c>[GenerateDbSets]</c> on the same context (mirrors <c>[GenerateElarionIdentity]</c>).
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionResourceGrants(SnakeCase = true)]
/// public sealed partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options) {
///     protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionResourceGrantsAttribute : Attribute {
    /// <summary>Whether to use snake_case table/column/index names. Defaults to <c>true</c>.</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>
    /// The table name, or <c>null</c> for the default (<c>elarion_resource_grants</c> /
    /// <c>ElarionResourceGrants</c> depending on <see cref="SnakeCase"/>).
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>The schema, or <c>null</c> for the provider's default schema.</summary>
    public string? Schema { get; set; }
}
