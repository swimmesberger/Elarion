namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Declares that the annotated partial <c>DbContext</c> hosts the Elarion audit-log table. The Elarion auditing
/// source generator emits the <c>DbSet&lt;AuditLogEntry&gt;</c> property and applies the model configuration (via
/// <see cref="AuditingModelBuilderExtensions.UseElarionAuditing"/>) through the EF generator's
/// model-configuration seam — so the host writes neither the DbSet nor the table mapping. Requires
/// <c>[GenerateDbSets]</c> on the same context (mirrors <c>[GenerateElarionIdempotencyKeys]</c>).
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionAuditing]
/// public sealed partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options) {
///     protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionAuditingAttribute : Attribute {
    /// <summary>Whether to use snake_case table/column/index names. Defaults to <c>true</c>.</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>
    /// The table name, or <c>null</c> for the default (<c>elarion_audit_log</c> / <c>ElarionAuditLog</c>
    /// depending on <see cref="SnakeCase"/>).
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>The schema, or <c>null</c> for the provider's default schema.</summary>
    public string? Schema { get; set; }
}
