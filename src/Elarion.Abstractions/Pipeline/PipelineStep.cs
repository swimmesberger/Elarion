namespace Elarion.Abstractions.Pipeline;

/// <summary>
/// One decorator in a handler's <see cref="IHandlerPipeline">resolved pipeline</see>: the decorator's open
/// generic type definition (e.g. <c>typeof(AuthorizationDecorator&lt;,&gt;)</c>) and whether it attached
/// through a runtime gate.
/// </summary>
/// <param name="Decorator">
/// The decorator's <b>open generic</b> type definition, so it compares equal across handlers
/// (<c>step.Decorator == typeof(AuditCommitDecorator&lt;,&gt;)</c>), rather than a per-handler closed type.
/// </param>
/// <param name="Conditional">
/// <see langword="false"/> for an unconditional framework decorator that always attaches for this handler;
/// <see langword="true"/> when attachment went through a runtime gate — a soft-attached decorator (audit, the
/// idempotency inbox) whose presence depends on whether a backing service is registered, or a
/// <c>[DecoratorList]</c> decorator with an <c>AppliesTo</c> predicate. A conditional step's presence can
/// therefore differ between processes; an unconditional one cannot.
/// </param>
public readonly record struct PipelineStep(Type Decorator, bool Conditional);
