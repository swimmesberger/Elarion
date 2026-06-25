namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Marker attribute for source generation of DbSet properties and configuration application.
/// Apply to a partial DbContext interface to auto-generate:
/// <list type="bullet">
///   <item><description><c>DbSet&lt;T&gt;</c> properties for each entity configured by an
///     <c>[EntityConfiguration]</c> class</description></item>
///   <item><description><c>DbSet&lt;T&gt;</c> properties on implementing DbContext classes</description></item>
///   <item><description><c>ConfigureEntities(ModelBuilder)</c> on implementing DbContext classes that
///     applies every discovered <c>[EntityConfiguration]</c></description></item>
/// </list>
/// </summary>
/// <remarks>
/// The entity set is driven entirely by <c>[EntityConfiguration]</c> — there is no separate entity
/// marker. This interface only selects <em>which</em> partial context to fill and, via
/// <see cref="Scopes"/>, which configured entities it should include.
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class GenerateDbSetsAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateDbSetsAttribute"/> class.
    /// </summary>
    /// <param name="scopes">
    /// Optional context scopes generated for this interface. Omit scopes to include every
    /// <c>[EntityConfiguration]</c>.
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
