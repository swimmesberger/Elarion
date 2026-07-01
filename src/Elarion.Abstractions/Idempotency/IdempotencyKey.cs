namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// The idempotency key captured at a transport boundary, carried through the dispatch-scope rail
/// (<see cref="Elarion.Abstractions.Dispatch.DispatchScopeContext"/>) to the per-call
/// <see cref="IIdempotencyKeyAccessor"/>. A dedicated type (rather than a bare <c>string</c>) so it never
/// collides with other string-keyed dispatch-scope values.
/// </summary>
/// <param name="Value">The client-supplied key value.</param>
public sealed record IdempotencyKey(string Value);
