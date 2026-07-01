namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Manages resource shares: who (a user or a role) may perform which operation on which resource. The shared
/// grants are what a <c>[ResourceFilter(Shared = true)]</c> data-level predicate consults (as an indexed
/// <c>EXISTS</c> over the caller's principals) and what the database-backed resource point-check evaluates.
/// </summary>
/// <remarks>
/// <para>
/// The default backend persists grants in the application's database (the only external system); the contract
/// is storage-neutral so an alternative backend could implement it. Granting the same
/// (resource, principal, operation) twice is idempotent — the default EF Core backend claims it with an
/// <c>INSERT … ON CONFLICT DO NOTHING</c>, so a concurrent duplicate never raises a unique violation that would
/// poison the caller's ambient transaction.
/// </para>
/// <para>
/// <b>Matching is case-sensitive (<see cref="System.StringComparison.Ordinal"/>).</b> The
/// <see cref="ResourceGrant.ResourceType"/> discriminator, <see cref="ResourceGrant.ResourceId"/>, and the
/// principal's kind/id (a user id or a role name) are compared exactly as stored against the point check's
/// discriminator (the resource type's <see cref="System.Type.FullName"/> by default) and the caller's
/// <c>ICurrentUser</c> user id/role names. A casing mismatch fails closed (denies); it never grants access. Store
/// the discriminator consistently with the matching <c>[ResourceFilter].ResourceTypeName</c> /
/// <c>[RequireResource].ResourceTypeName</c>.
/// </para>
/// </remarks>
public interface IResourceGrantStore {
    /// <summary>Shares the resource with the principal for the operation (idempotent).</summary>
    ValueTask GrantAsync(ResourceGrant grant, CancellationToken ct);

    /// <summary>Removes a previously granted share (a no-op if it does not exist).</summary>
    ValueTask RevokeAsync(ResourceGrant grant, CancellationToken ct);

    /// <summary>Returns every grant on the given resource.</summary>
    ValueTask<IReadOnlyList<ResourceGrant>> GetGrantsAsync(string resourceType, string resourceId, CancellationToken ct);
}
