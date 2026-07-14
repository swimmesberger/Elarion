namespace Elarion.Diagnostics;

/// <summary>
/// Configures the built-in user-context enricher that <see cref="Pipeline.ObservabilityDecorator{TRequest,TResponse}"/>
/// runs — the default-on contribution that stamps the calling user's identity onto the handler span and log scope.
/// </summary>
/// <remarks>
/// The built-in enricher is active even without registering this type; register (and mutate) it via
/// <see cref="UserContextEnrichmentServiceCollectionExtensions.AddElarionUserContextEnrichment"/> only to opt out
/// (<see cref="Enabled"/>), narrow the payload, or opt into email. The default payload is <c>user.id</c> +
/// <c>user.roles</c> + <c>user.permissions</c>. Email is PII and off by default. User identity is never recorded on
/// metrics (unbounded cardinality) — it rides only per-span attributes and the per-execution log scope. This governs
/// only the built-in enricher; a host's own <c>IHandlerContextEnricher</c> registrations are independent.
/// </remarks>
public sealed class UserContextEnrichmentOptions {
    /// <summary>
    /// Whether the built-in user-context enricher runs. Defaults to <see langword="true"/>; set to
    /// <see langword="false"/> to opt out (host-registered enrichers still run).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Emit the caller's roles as the <c>user.roles</c> span tag and <c>UserRoles</c> log-scope key. Defaults to
    /// <see langword="true"/>. Bounded by <see cref="MaxItems"/>.
    /// </summary>
    public bool IncludeRoles { get; set; } = true;

    /// <summary>
    /// Emit the caller's permissions (claims of the configured permission claim type) as the <c>user.permissions</c>
    /// span tag and <c>UserPermissions</c> log-scope key. Defaults to <see langword="true"/>. Bounded by
    /// <see cref="MaxItems"/>.
    /// </summary>
    public bool IncludePermissions { get; set; } = true;

    /// <summary>
    /// Emit the caller's email as the <c>user.email</c> span tag and <c>UserEmail</c> log-scope key. Defaults to
    /// <see langword="false"/> — email is PII, so a host opts in and owns redaction/retention in its telemetry backend.
    /// </summary>
    public bool IncludeEmail { get; set; }

    /// <summary>
    /// Upper bound on how many roles/permissions are joined into a tag, so the span attribute stays bounded for
    /// callers with many roles or permissions. Defaults to 16.
    /// </summary>
    public int MaxItems { get; set; } = 16;
}
