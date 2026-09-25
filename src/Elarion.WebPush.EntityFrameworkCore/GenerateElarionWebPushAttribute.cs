namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// Opts a <c>[GenerateDbSets]</c> context into the Web Push tables: the bundled generator adds the
/// <c>DbSet&lt;PushSubscriptionEntity&gt;</c>/<c>DbSet&lt;VapidKeyEntity&gt;</c> and applies
/// <c>UseElarionWebPush</c> through the EF generator's per-feature model-configuration seam.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionWebPush]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionWebPushAttribute : Attribute {
    /// <summary>Whether table/column names default to snake_case (the Elarion default).</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>Overrides the push subscription table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? SubscriptionTableName { get; set; }

    /// <summary>Overrides the VAPID key table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? VapidKeyTableName { get; set; }

    /// <summary>Optional schema.</summary>
    public string? Schema { get; set; }
}
