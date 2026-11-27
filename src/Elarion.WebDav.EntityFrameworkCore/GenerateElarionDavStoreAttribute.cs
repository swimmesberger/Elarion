namespace Elarion.WebDav.EntityFrameworkCore;

/// <summary>
/// Opts a <c>[GenerateDbSets]</c> context into the published-collection store: the bundled generator adds the
/// <c>ElarionDavCollections</c>, <c>ElarionDavObjects</c> and <c>ElarionDavChanges</c> sets and applies
/// <c>UseElarionDavStore</c> through the EF generator's per-feature model-configuration seam.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionDavStore]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionDavStoreAttribute : Attribute {
    /// <summary>Whether table and column names are snake_case (the Elarion default).</summary>
    public bool SnakeCase { get; init; } = true;

    /// <summary>Optional schema.</summary>
    public string? Schema { get; init; }
}
