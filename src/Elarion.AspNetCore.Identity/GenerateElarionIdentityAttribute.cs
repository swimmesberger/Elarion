using Microsoft.AspNetCore.Identity;

namespace Elarion.AspNetCore.Identity;

/// <summary>
/// Declares that the annotated partial <c>DbContext</c> hosts ASP.NET Core Identity for the given user,
/// role, and key types. The Elarion Identity source generator emits the Identity <c>DbSet</c> properties
/// and applies the Identity model configuration (via <see cref="IdentityModelBuilderExtensions.ApplyElarionIdentity"/>)
/// through the EF generator's <c>OnEntitiesConfigured</c> seam — so the context composes Identity instead
/// of inheriting <c>IdentityDbContext</c>. Requires <c>[GenerateDbSets]</c> on the same context.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionIdentity&lt;ApplicationUser, ApplicationRole, Guid&gt;(SnakeCase = true)]
/// public sealed partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options) {
///     protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureEntities(modelBuilder);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionIdentityAttribute<TUser, TRole, TKey> : Attribute
    where TUser : IdentityUser<TKey>
    where TRole : IdentityRole<TKey>
    where TKey : IEquatable<TKey> {
    /// <summary>Whether to use snake_case table/column/index names. Defaults to <c>true</c>.</summary>
    public bool SnakeCase { get; set; } = true;
}
