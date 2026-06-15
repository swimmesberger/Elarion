namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Marker attribute for source generation of DbSet properties and configuration application.
/// Apply to a partial DbContext interface to auto-generate:
/// <list type="bullet">
///   <item><c>DbSet&lt;T&gt;</c> properties for each <c>[DbEntity]</c>-marked class</item>
///   <item><c>DbSet&lt;T&gt;</c> properties on implementing DbContext classes</item>
///   <item><c>ConfigureEntities(ModelBuilder)</c> on implementing DbContext classes</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class GenerateDbSetsAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateDbSetsAttribute"/> class.
    /// </summary>
    /// <param name="scopes">
    /// Optional context scopes generated for this interface. Omit scopes to include every <c>[DbEntity]</c>.
    /// </param>
    public GenerateDbSetsAttribute(params string[] scopes)
    {
        Scopes = scopes;
    }

    /// <summary>
    /// Gets the context scopes generated for this interface.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }
}
