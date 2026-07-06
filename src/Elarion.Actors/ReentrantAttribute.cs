namespace Elarion.Actors;

/// <summary>
/// Opts an <c>[Actor]</c> class into Orleans-style turn-based interleaving: while one message is
/// awaiting, the mailbox may start processing another. Turns never run in parallel — execution stays
/// single-threaded (an exclusive scheduler serializes every continuation) — but state observed
/// across an <c>await</c> may have been changed by an interleaved message.
/// </summary>
/// <remarks>
/// Use this to keep a slow actor responsive (e.g. a method awaiting I/O should not block cheap
/// reads) or to break actor→actor request cycles. Do not use it casually: reasoning about
/// interleaved turns is exactly the complexity the non-reentrant default removes. Caveat: an
/// <c>await</c> using <c>ConfigureAwait(false)</c> <em>in the actor's own methods</em> escapes the
/// exclusive scheduler and forfeits the single-threaded guarantee for the rest of that method —
/// don't use it in code that touches actor state. Libraries the actor calls may freely use
/// <c>ConfigureAwait(false)</c> internally (context capture is per-method; the actor method still
/// resumes on its scheduler), but a state-mutating delegate passed into a library runs wherever the
/// library invokes it and escapes regardless.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ReentrantAttribute : Attribute;
