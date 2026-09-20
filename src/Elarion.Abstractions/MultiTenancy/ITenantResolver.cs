namespace Elarion.Abstractions.MultiTenancy;

/// <summary>
/// Produces the current tenant id for a scope. The replaceable half of <see cref="ITenantContext"/>: the
/// shipped default reads a claim from the current principal, and an application that derives tenancy some
/// other way — a membership table, a host header, a device registration — registers its own implementation
/// without any entity, handler, or query changing.
/// </summary>
/// <remarks>
/// Resolution is <b>synchronous</b> on purpose. The read leg is an Entity Framework Core query filter, which is
/// evaluated while a query is being translated and cannot await anything. An application whose resolution needs
/// I/O does it where awaiting is legal — middleware, a dispatch-scope initializer, a job loop — and then calls
/// <see cref="ITenantContext.Scope"/> with the result; the resolver is for values already in hand.
/// </remarks>
public interface ITenantResolver {
    /// <summary>
    /// Returns the current tenant id, or <see langword="null"/> when the caller belongs to no tenant. Called at
    /// most once per scope; the result is cached by <see cref="ITenantContext"/>.
    /// </summary>
    string? Resolve();
}
