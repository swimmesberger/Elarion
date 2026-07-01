namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Manages resource shares: who (a user or a role) may perform which operation on which resource. The shared
/// grants are what a <c>[ResourceFilter(Shared = true)]</c> data-level predicate consults (as an indexed
/// <c>EXISTS</c> over the caller's principals) and what the database-backed resource point-check evaluates.
/// </summary>
/// <remarks>
/// The default backend persists grants in the application's database (the only external system); the contract
/// is storage-neutral so an alternative backend could implement it. Granting the same
/// (resource, principal, operation) twice is idempotent.
/// </remarks>
public interface IResourceGrantStore {
    /// <summary>Shares the resource with the principal for the operation (idempotent).</summary>
    ValueTask GrantAsync(ResourceGrant grant, CancellationToken ct);

    /// <summary>Removes a previously granted share (a no-op if it does not exist).</summary>
    ValueTask RevokeAsync(ResourceGrant grant, CancellationToken ct);

    /// <summary>Returns every grant on the given resource.</summary>
    ValueTask<IReadOnlyList<ResourceGrant>> GetGrantsAsync(string resourceType, string resourceId, CancellationToken ct);
}
