namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Identifies the Entity Framework Core database provider an Elarion application targets, so source
/// generators can emit provider-optimized variants while keeping the default output portable across
/// every provider.
/// </summary>
/// <remarks>
/// The numeric values are part of the contract read by the source generators and must not be
/// renumbered. <see cref="Portable"/> is the default and emits provider-neutral code.
/// </remarks>
public enum EfCoreProvider {
    /// <summary>
    /// Provider-neutral output that translates on any EF Core provider. This is the default.
    /// </summary>
    Portable = 0,

    /// <summary>
    /// PostgreSQL via <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>. Enables PostgreSQL-specific
    /// optimizations such as row-value (<c>(a, b) &gt; (x, y)</c>) keyset seek predicates, which let
    /// a composite index satisfy the seek in a single range scan.
    /// </summary>
    Npgsql = 1
}
