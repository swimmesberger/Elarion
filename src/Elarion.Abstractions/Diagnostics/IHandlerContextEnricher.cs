namespace Elarion.Abstractions.Diagnostics;

/// <summary>
/// Contributes diagnostic context — trace-span tags and log-scope items — for a single handler execution. The
/// framework runs every registered enricher just inside the handler tracing span, applies the accumulated tags to
/// that span, and opens one logging scope carrying the accumulated items around the handler.
/// </summary>
/// <remarks>
/// <para>
/// This is the extension point for request/user context in traces and logs. Elarion ships a built-in enricher that
/// emits <c>user.id</c>, <c>user.roles</c>, and <c>user.permissions</c> from <see cref="Identity.ICurrentUser"/>
/// (see <c>UserContextEnrichmentOptions</c>); a host adds its own — a tenant id, a request source, a correlation
/// value — by implementing this interface and registering it with <c>AddElarionHandlerContextEnricher</c>.
/// Registrations compose (they do not replace each other), so a host's enrichers run alongside the built-in one.
/// Disable the built-in via <c>AddElarionUserContextEnrichment(o =&gt; o.Enabled = false)</c> while keeping your own.
/// </para>
/// <para>
/// Implementations are resolved per handler execution from the ambient scope, so they may inject scoped services
/// (e.g. <see cref="Identity.ICurrentUser"/>). <see cref="Enrich"/> must not throw and should be cheap — it runs on
/// every handler invocation. Anonymous executions (scheduler, post-commit delivery) still invoke enrichers, so an
/// enricher that reads user identity should guard on <see cref="Identity.ICurrentUser.IsAuthenticated"/>.
/// </para>
/// </remarks>
public interface IHandlerContextEnricher {
    /// <summary>Writes trace tags and/or log-scope items for the current handler execution into <paramref name="context"/>.</summary>
    void Enrich(HandlerEnrichmentContext context);
}
