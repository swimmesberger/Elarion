using System.Diagnostics;
using Elarion.Abstractions.Auditing;

namespace Elarion.Auditing;

/// <summary>
/// The scoped <see cref="IAuditScope"/> implementation: one frame per audited handler invocation, pushed by
/// the outer audit decorator and drained into an <see cref="AuditRecord"/> by whichever decorator records the
/// outcome. Frames nest so a typed in-process call from one audited handler into another (same DI scope, e.g.
/// via <c>IHandlerSender</c> or a module facade) records two independent records.
/// </summary>
/// <remarks>
/// The lifecycle members (<see cref="Begin"/>/<see cref="End"/>/<see cref="BeginAttempt"/>/
/// <see cref="BuildRecord"/>/<see cref="MarkRecorded"/>) are called by the framework's audit decorators —
/// application code only ever uses the <see cref="IAuditScope"/> surface. Not thread-safe: like the rest of
/// the per-invocation pipeline state it is written sequentially by one logical request.
/// </remarks>
public sealed class AuditScope(TimeProvider? timeProvider = null) : IAuditScope {
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Stack<Frame> _frames = new();

    /// <inheritdoc />
    public bool IsActive => _frames.Count > 0;

    /// <summary>Whether the current invocation's record was already written (by the inner success recorder).</summary>
    public bool Recorded => _frames.TryPeek(out var frame) && frame.Recorded;

    /// <summary>Opens a frame for one audited handler invocation. Called by the outer audit decorator.</summary>
    public void Begin(string action, string? module, string? userId, string? defaultResourceType) =>
        _frames.Push(new Frame {
            Action = action,
            Module = module,
            UserId = userId,
            DefaultResourceType = defaultResourceType,
        });

    /// <summary>Closes the current frame. Called by the outer audit decorator, always (finally).</summary>
    public void End() => _frames.TryPop(out _);

    /// <summary>
    /// Resets the current frame's accumulated changes, details, and resource for a fresh handler attempt.
    /// Called by the inner audit decorator before each attempt: resilience retries re-enter it, and a record
    /// must never mix a rolled-back attempt's diffs with the succeeding attempt's.
    /// </summary>
    public void BeginAttempt() {
        if (!_frames.TryPeek(out var frame))
            return;

        frame.Changes.Clear();
        frame.Details.Clear();
        frame.ResourceType = null;
        frame.ResourceId = null;
        frame.ParentResourceType = null;
        frame.ParentResourceId = null;
    }

    /// <summary>Marks the current invocation's record as written, so the outer decorator does not record again.</summary>
    public void MarkRecorded() {
        if (_frames.TryPeek(out var frame))
            frame.Recorded = true;
    }

    /// <summary>Drains the current frame into a record. The frame stays open (End closes it).</summary>
    public AuditRecord BuildRecord(AuditOutcome outcome, string? errorKind) {
        if (!_frames.TryPeek(out var frame))
            throw new InvalidOperationException("No audit frame is active.");

        return new AuditRecord {
            Id = Guid.CreateVersion7(),
            OccurredAt = _timeProvider.GetUtcNow(),
            Action = frame.Action,
            Module = frame.Module,
            UserId = frame.UserId,
            ResourceType = frame.ResourceType ?? frame.DefaultResourceType,
            ResourceId = frame.ResourceId,
            ParentResourceType = frame.ParentResourceType,
            ParentResourceId = frame.ParentResourceId,
            Outcome = outcome,
            ErrorKind = errorKind,
            CorrelationId = Activity.Current?.TraceId.ToString(),
            Changes = frame.Changes.Count > 0 ? frame.Changes.ToArray() : [],
            Details = frame.Details.Count > 0 ? new Dictionary<string, string>(frame.Details) : EmptyDetails,
        };
    }

    /// <inheritdoc />
    public void SetResource(string type, string id, string? parentType = null, string? parentId = null) {
        if (!_frames.TryPeek(out var frame))
            return;

        frame.ResourceType = type;
        frame.ResourceId = id;
        frame.ParentResourceType = parentType;
        frame.ParentResourceId = parentId;
    }

    /// <inheritdoc />
    public void AddChange(AuditChange change) {
        if (_frames.TryPeek(out var frame))
            frame.Changes.Add(change);
    }

    /// <inheritdoc />
    public void AddDetail(string name, string value) {
        if (_frames.TryPeek(out var frame))
            frame.Details[name] = value;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyDetails =
        new Dictionary<string, string>(capacity: 0);

    private sealed class Frame {
        public required string Action { get; init; }
        public string? Module { get; init; }
        public string? UserId { get; init; }
        public string? DefaultResourceType { get; init; }
        public string? ResourceType { get; set; }
        public string? ResourceId { get; set; }
        public string? ParentResourceType { get; set; }
        public string? ParentResourceId { get; set; }
        public bool Recorded { get; set; }
        public List<AuditChange> Changes { get; } = [];
        public Dictionary<string, string> Details { get; } = new(StringComparer.Ordinal);
    }
}
