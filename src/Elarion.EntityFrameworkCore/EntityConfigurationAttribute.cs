namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Marks an <c>IEntityTypeConfiguration&lt;TEntity&gt;</c> implementation as the single source of truth
/// for an entity's participation in a generated context. The EF Core source generator reads every
/// <c>[EntityConfiguration]</c> class to drive both halves of model wiring:
/// <list type="bullet">
///   <item><description>a <c>DbSet&lt;T&gt;</c> property for each <c>IEntityTypeConfiguration&lt;T&gt;</c>
///     the class implements (on the <c>[GenerateDbSets]</c> interface and the implementing context), and</description></item>
///   <item><description>a direct <c>Configure(...)</c> call applying the configuration from the generated
///     <c>ConfigureEntities(ModelBuilder)</c> method (no reflection scanning).</description></item>
/// </list>
/// </summary>
/// <remarks>
/// There is no separate entity marker — a <em>configured</em> entity is a <em>discovered</em> entity, so
/// every entity reachable through a generated context has an explicit configuration. A single
/// configuration class may implement <c>IEntityTypeConfiguration&lt;T&gt;</c> more than once; each
/// implemented entity contributes its own <c>DbSet</c> and its own <c>Configure(...)</c> call. The class
/// is discovered structurally — it is found by the <c>[EntityConfiguration]</c> attribute and the
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> interfaces it implements — so it may live wherever its module
/// keeps it, including a different assembly than the entity (cross-assembly discovery reads the emitted
/// per-assembly manifest).
/// </remarks>
/// <example>
/// <code>
/// [EntityConfiguration]
/// public sealed class InvoiceConfiguration : IEntityTypeConfiguration&lt;Invoice&gt; {
///     public void Configure(EntityTypeBuilder&lt;Invoice&gt; builder) { /* ... */ }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class EntityConfigurationAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="EntityConfigurationAttribute"/> class.
    /// </summary>
    /// <param name="scopes">
    /// Optional context scopes every entity this configuration configures belongs to. Omit scopes to
    /// participate only in unscoped/global generated contexts.
    /// </param>
    public EntityConfigurationAttribute(params string[] scopes)
    {
        Scopes = scopes;
    }

    /// <summary>
    /// Gets the context scopes every entity configured by this configuration belongs to.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }
}
