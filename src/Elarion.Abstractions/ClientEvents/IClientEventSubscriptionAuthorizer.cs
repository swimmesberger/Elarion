namespace Elarion.Abstractions.ClientEvents;

/// <summary>
/// Decides whether the <em>current user</em> may subscribe to a resource-scoped client-event topic
/// (e.g. is the caller a member of <c>"customer:42"</c>?). Resolved from the request scope, so
/// implementations inject <c>ICurrentUser</c> and their <c>DbContext</c> like any scoped service.
/// </summary>
/// <remarks>
/// The seam is <b>fail-closed</b>: a resource-scoped subscription is denied when no authorizer is registered
/// or when the authorizer returns <see langword="false"/>. Global and user scopes never consult this seam —
/// user scope is always derived from the authenticated user and cannot name someone else. Subscribe-time
/// checks are a UX projection, never the security boundary: the data itself is only ever fetched through
/// handlers carrying the real authorization gates.
/// </remarks>
public interface IClientEventSubscriptionAuthorizer {
    /// <summary>Returns whether the current user may observe <paramref name="subscription"/>.</summary>
    /// <param name="subscription">The requested topic + resource scope.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask<bool> AuthorizeAsync(ClientEventSubscription subscription, CancellationToken ct);
}
