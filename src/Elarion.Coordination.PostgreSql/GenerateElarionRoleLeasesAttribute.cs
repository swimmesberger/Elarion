namespace Elarion.Coordination.PostgreSql;

/// <summary>
/// Opts a <c>[GenerateDbSets]</c> context into the role lease table (ADR-0049): the bundled
/// generator adds the <c>DbSet&lt;RoleLeaseEntity&gt;</c> and applies <c>UseElarionRoleLeases</c>
/// through the EF generator's per-feature model-configuration seam.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionRoleLeases]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionRoleLeasesAttribute : Attribute {
    /// <summary>Whether table/column names default to snake_case (the Elarion default).</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>Overrides the table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? TableName { get; set; }

    /// <summary>Optional schema.</summary>
    public string? Schema { get; set; }
}
