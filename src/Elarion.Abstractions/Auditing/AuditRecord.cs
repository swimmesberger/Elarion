namespace Elarion.Abstractions.Auditing;

/// <summary>
/// One structured audit fact: who performed which action on which resource, when, with what outcome — plus
/// the field-level <see cref="Changes"/> and free-form <see cref="Details"/> the invocation's
/// <see cref="IAuditScope"/> accumulated. Built by the framework's audit decorators and written through
/// <see cref="IAuditTrail"/>.
/// </summary>
/// <remarks>
/// The record is metadata-shaped by design: it never carries the request payload. Anything beyond the
/// structured fields goes through the explicit, app-controlled <see cref="Details"/> bag (ADR-0045).
/// </remarks>
public sealed record AuditRecord {
    /// <summary>Client-assigned id (<see cref="Guid.CreateVersion7()"/> — time-ordered).</summary>
    public required Guid Id { get; init; }

    /// <summary>When the invocation completed.</summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>The full handler name (<c>{module}.{operation}</c>).</summary>
    public required string Action { get; init; }

    /// <summary>The owning module (the first segment of <see cref="Action"/>); <see langword="null"/> when unscoped.</summary>
    public string? Module { get; init; }

    /// <summary>The acting user's id from <c>ICurrentUser</c>; <see langword="null"/> for anonymous or system callers.</summary>
    public string? UserId { get; init; }

    /// <summary>The audited resource type (e.g. <c>"property"</c>); from the scope or <c>[Auditable(Resource = …)]</c>.</summary>
    public string? ResourceType { get; init; }

    /// <summary>The audited resource id, invariant-formatted.</summary>
    public string? ResourceId { get; init; }

    /// <summary>Optional parent/aggregate resource type (e.g. the property a fire extinguisher belongs to).</summary>
    public string? ParentResourceType { get; init; }

    /// <summary>Optional parent/aggregate resource id.</summary>
    public string? ParentResourceId { get; init; }

    /// <summary>The invocation outcome.</summary>
    public required AuditOutcome Outcome { get; init; }

    /// <summary>The <c>ErrorKind</c> name for non-success outcomes (e.g. <c>"Conflict"</c>); <see langword="null"/> on success.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>The distributed-trace id current at recording time, linking the record to its span.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Field-level changes; empty when nothing was captured or contributed.</summary>
    public IReadOnlyList<AuditChange> Changes { get; init; } = [];

    /// <summary>App-supplied, string-valued facts (display names, rule ids, …). Keep values free of secrets.</summary>
    public IReadOnlyDictionary<string, string> Details { get; init; } = EmptyDetails;

    private static readonly IReadOnlyDictionary<string, string> EmptyDetails =
        new Dictionary<string, string>(0);
}
