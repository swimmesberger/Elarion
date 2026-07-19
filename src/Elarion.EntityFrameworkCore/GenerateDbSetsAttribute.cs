namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Marker attribute for source generation of DbSet properties and configuration application. Apply to a
/// partial <c>DbContext</c> class to auto-generate, on that class:
/// <list type="bullet">
///   <item><description>a <c>DbSet&lt;T&gt;</c> property for each entity configured by an
///     <c>[EntityConfiguration]</c> class</description></item>
///   <item><description>a <c>ConfigureEntities(ModelBuilder)</c> method that applies every discovered
///     <c>[EntityConfiguration]</c> (call it from <c>OnModelCreating</c>)</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The database is application logic, not an abstraction: handlers work directly against the concrete
/// <c>DbContext</c> (LINQ, raw SQL, provider functions), so there is no generated context interface — the
/// DbSets are a concern of the implementation. The entity set is driven entirely by
/// <c>[EntityConfiguration]</c>; this attribute only selects <em>which</em> partial context to fill and,
/// via <see cref="Scopes"/>, which configured entities it should include.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateDbSetsAttribute : Attribute {
    /// <summary>
    /// Initializes a new instance of the <see cref="GenerateDbSetsAttribute"/> class.
    /// </summary>
    /// <param name="scopes">
    /// Optional context scopes generated for this context. Omit scopes to include every
    /// <c>[EntityConfiguration]</c>.
    /// </param>
    public GenerateDbSetsAttribute(params string[] scopes) {
        Scopes = scopes;
    }

    /// <summary>
    /// Gets the context scopes generated for this context.
    /// </summary>
    public IReadOnlyList<string> Scopes { get; }
}
