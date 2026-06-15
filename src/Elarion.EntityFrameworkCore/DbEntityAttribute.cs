namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Marks an entity class for inclusion in the DbContext.
/// Source generator scans for this attribute to auto-generate DbSet properties.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class DbEntityAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DbEntityAttribute"/> class.
    /// </summary>
    /// <param name="scopes">
    /// Optional context scopes this entity belongs to. Omit scopes to participate in unscoped/global contexts.
    /// </param>
    public DbEntityAttribute(params string[] scopes)
    {
        Scopes = scopes;
    }

    /// <summary>
    /// Gets the context scopes this entity belongs to.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }
}
