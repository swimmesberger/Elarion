namespace Elarion.Actors.PostgreSql;

/// <summary>
/// Opts a <c>[GenerateDbSets]</c> context into the actor snapshot table: the bundled generator adds
/// the <c>DbSet&lt;ActorSnapshotEntity&gt;</c> and applies <c>UseElarionActorSnapshots</c> through
/// the EF generator's per-feature model-configuration seam.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionActorSnapshots]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionActorSnapshotsAttribute : Attribute {
    /// <summary>Whether table/column names default to snake_case (the Elarion default).</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>Overrides the table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? TableName { get; set; }

    /// <summary>Optional schema.</summary>
    public string? Schema { get; set; }
}
