namespace Elarion.AppPasswords.EntityFrameworkCore;

/// <summary>
/// Opts a <c>[GenerateDbSets]</c> context into the app-password table: the bundled generator adds a
/// <c>DbSet&lt;AppPasswordEntity&gt; ElarionAppPasswords</c> and applies <c>UseElarionAppPasswords</c> through the EF
/// generator's per-feature model-configuration seam.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionAppPasswords]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionAppPasswordsAttribute : Attribute {
    /// <summary>Whether table and column names default to snake_case (the Elarion default).</summary>
    public bool SnakeCase { get; init; } = true;

    /// <summary>Overrides the table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? TableName { get; init; }

    /// <summary>Optional schema.</summary>
    public string? Schema { get; init; }
}
