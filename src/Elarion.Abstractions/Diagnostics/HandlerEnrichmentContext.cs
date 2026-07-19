namespace Elarion.Abstractions.Diagnostics;

/// <summary>
/// The sink an <see cref="IHandlerContextEnricher"/> writes to during one handler execution. Trace tags land on the
/// handler span; scope items become a single logging scope wrapping the handler.
/// </summary>
/// <remarks>
/// Keep the two key styles idiomatic to their sink: trace tags use OpenTelemetry semantic-convention keys
/// (<c>user.id</c>, <c>user.roles</c>, <c>tenant.id</c>); log-scope items use PascalCase keys (<c>UserId</c>,
/// <c>UserRoles</c>). A value that should appear in both a trace and logs is added with both <see cref="SetTag"/>
/// and <see cref="AddScopeItem"/>. Null or empty keys/values are ignored, so an enricher can add unconditionally.
/// The context accumulates nothing until first written, so an execution with no contributing enricher allocates
/// nothing.
/// </remarks>
public sealed class HandlerEnrichmentContext {
    private static readonly KeyValuePair<string, object>[] Empty = [];

    private List<KeyValuePair<string, object>>? _tags;
    private List<KeyValuePair<string, object>>? _scopeItems;

    /// <summary>
    /// Sets a tag on the handler trace span. Use an OpenTelemetry semantic-convention key (e.g. <c>user.id</c>).
    /// Ignored when <paramref name="key"/> is null/empty or <paramref name="value"/> is null. A later tag with the
    /// same key overwrites the earlier one on the span.
    /// </summary>
    public void SetTag(string key, object? value) {
        if (string.IsNullOrEmpty(key) || value is null) return;

        (_tags ??= []).Add(new KeyValuePair<string, object>(key, value));
    }

    /// <summary>
    /// Adds a structured item to the handler's log scope. Use a PascalCase key (e.g. <c>UserId</c>). Ignored when
    /// <paramref name="key"/> is null/empty or <paramref name="value"/> is null.
    /// </summary>
    public void AddScopeItem(string key, object? value) {
        if (string.IsNullOrEmpty(key) || value is null) return;

        (_scopeItems ??= []).Add(new KeyValuePair<string, object>(key, value));
    }

    /// <summary>Tags accumulated for the handler span. Drained by the runtime; enrichers write via <see cref="SetTag"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> Tags =>
        _tags ?? (IReadOnlyList<KeyValuePair<string, object>>)Empty;

    /// <summary>Items accumulated for the handler log scope. Drained by the runtime; enrichers write via <see cref="AddScopeItem"/>.</summary>
    public IReadOnlyList<KeyValuePair<string, object>> ScopeItems =>
        _scopeItems ?? (IReadOnlyList<KeyValuePair<string, object>>)Empty;
}
