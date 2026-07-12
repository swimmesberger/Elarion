namespace Elarion.Abstractions.Coordination;

/// <summary>
/// A cluster-wide <b>role lease</b> (ADR-0049): time-bounded, self-renewing single-holder election
/// for a named role — "which instance <em>is</em> X right now". The coarse third member of Elarion's
/// coordination taxonomy (scheduler claims = per work item, outbox leases = per message, role lease =
/// per role): an application holds a handful of long-lived roles, never per-key locks.
/// </summary>
/// <remarks>
/// Implementations are registered <b>keyed by role name</b>
/// (<c>GetRequiredKeyedService&lt;IRoleLease&gt;("maintenance")</c>) and answer
/// <see cref="IsHeld"/> from local state (no I/O — it is consulted on hot paths such as the actor
/// call gate and the outbox delivery cycle). <see cref="IsHeld"/> must turn <see langword="false"/>
/// before the underlying lease can have expired for another instance (a safety margin inside the
/// lease duration), so a losing holder stops acting before its successor can start. Consumers gate
/// per unit of work (per call, per polling cycle) rather than starting/stopping on leadership
/// changes. This is deliberately <em>not</em> a distributed-lock API: no per-key acquisition, no
/// lock scopes — per-work-item coordination belongs to scheduler claims and outbox leases.
/// </remarks>
public interface IRoleLease {
    /// <summary>The role this lease elects a holder for.</summary>
    string Role { get; }

    /// <summary>Whether this instance currently holds the role.</summary>
    bool IsHeld { get; }

    /// <summary>
    /// The instance currently believed to hold the role (for diagnostics/errors), or
    /// <see langword="null"/> when unknown.
    /// </summary>
    string? CurrentHolder { get; }

    /// <summary>
    /// The holder's advertised base address (ADR-0050), or <see langword="null"/> when the holder is
    /// unknown or does not advertise one. Like <see cref="IsHeld"/>, answered from local state
    /// refreshed at heartbeat cadence — consumers (e.g. the role-holder proxy) tolerate it lagging a
    /// failover by up to one renew interval.
    /// </summary>
    string? CurrentHolderAddress => null;
}
