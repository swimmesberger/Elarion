namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// The persisted audit record (ADR-0045): one append-only row per audited handler invocation. The structured
/// columns (action, user, resource, parent resource, outcome, time) are the searchable surface — the classic
/// "who changed what, filter by resource and user" query is an indexed <c>WHERE</c> over them — while
/// <see cref="Changes"/>/<see cref="Details"/> carry the display payload as canonical JSON.
/// </summary>
/// <remarks>
/// Rows are written by <see cref="EfCoreAuditTrail{TDbContext}"/> only: success records ride the business
/// transaction, denial/failure records arrive on a detached scope. The table is never updated or read by the
/// framework after the insert — querying and display are the application's (an app query handler pages over
/// the <c>DbSet</c> with keyset pagination).
/// </remarks>
public sealed class AuditLogEntry {
    /// <summary>Client-assigned id (Guid v7, time-ordered — ADR-0038).</summary>
    public required Guid Id { get; init; }

    /// <summary>When the invocation completed.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    /// <summary>The audited action (the handler's wire name, e.g. <c>"properties.update"</c>).</summary>
    public required string Action { get; init; }

    /// <summary>The owning module; <see langword="null"/> when the handler is unscoped.</summary>
    public string? Module { get; init; }

    /// <summary>The acting user's id; <see langword="null"/> for anonymous or system callers.</summary>
    public string? UserId { get; init; }

    /// <summary>The resource type acted on (e.g. <c>"property"</c>).</summary>
    public string? ResourceType { get; init; }

    /// <summary>The resource id, invariant-formatted.</summary>
    public string? ResourceId { get; init; }

    /// <summary>The parent/aggregate resource type, so the record surfaces on the parent's audit view.</summary>
    public string? ParentResourceType { get; init; }

    /// <summary>The parent/aggregate resource id.</summary>
    public string? ParentResourceId { get; init; }

    /// <summary>The outcome name (<c>"Succeeded"</c>/<c>"Failed"</c>/<c>"Denied"</c>), stored as text for searchability.</summary>
    public required string Outcome { get; init; }

    /// <summary>The <c>ErrorKind</c> name for non-success outcomes; <see langword="null"/> on success.</summary>
    public string? ErrorKind { get; init; }

    /// <summary>The distributed-trace id current at recording time.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Field-level changes as a canonical-JSON array; <see langword="null"/> when none were captured.</summary>
    public string? Changes { get; init; }

    /// <summary>App-supplied details as a canonical-JSON object; <see langword="null"/> when none were added.</summary>
    public string? Details { get; init; }
}
