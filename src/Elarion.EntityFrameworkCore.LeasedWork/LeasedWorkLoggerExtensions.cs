using Microsoft.Extensions.Logging;

namespace Elarion.EntityFrameworkCore.LeasedWork;

/// <summary>
/// The log lines every leased-work consumer writes after a finalize, stated once: a lost claim and a row that changed
/// while the worker held it.
/// </summary>
/// <example>
/// <code>
/// var outcome = await db.Deliveries.FinalizeClaimAsync(delivery.Id, lockId, finalize, ct);
/// logger.LogFinalizeOutcome(outcome, "delivery", delivery.Id);
/// </code>
/// </example>
public static class LeasedWorkLoggerExtensions {
    private static readonly Action<ILogger, string, Guid, Exception?> ClaimLost = LoggerMessage.Define<string, Guid>(
        LogLevel.Warning,
        new EventId(1, "LeasedWorkClaimLost"),
        "The claim on {Queue} row {Id} was lost: the lease expired and another worker took the row, or the row was " +
        "deleted meanwhile. Nothing was written.");

    private static readonly Action<ILogger, string, Guid, Exception?> Ineligible = LoggerMessage.Define<string, Guid>(
        LogLevel.Information,
        new EventId(2, "LeasedWorkIneligible"),
        "{Queue} row {Id} changed while it was claimed and no longer needs this result; the claim was released without it.");

    /// <summary>
    /// Logs a <see cref="LeasedWorkFinalizeOutcome.ClaimLost"/> as a warning and an
    /// <see cref="LeasedWorkFinalizeOutcome.Ineligible"/> as information; a finalized row logs nothing.
    /// </summary>
    /// <param name="logger">The worker's logger.</param>
    /// <param name="outcome">What the finalize did.</param>
    /// <param name="queue">A short name for the queue, for the message (<c>"delivery"</c>).</param>
    /// <param name="id">The row's id.</param>
    /// <returns>Whether the setters applied (<see cref="LeasedWorkFinalizeOutcome.Finalized"/>).</returns>
    public static bool LogFinalizeOutcome(this ILogger logger, LeasedWorkFinalizeOutcome outcome, string queue, Guid id) {
        ArgumentNullException.ThrowIfNull(logger);
        switch (outcome) {
            case LeasedWorkFinalizeOutcome.ClaimLost:
                ClaimLost(logger, queue, id, null);
                return false;
            case LeasedWorkFinalizeOutcome.Ineligible:
                Ineligible(logger, queue, id, null);
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// Logs a lost claim as a warning when <paramref name="finalized"/> — the result of the <see cref="bool"/>-returning
    /// <c>FinalizeClaimAsync</c>, <c>ReleaseClaimAsync</c> or <c>RenewClaimAsync</c> — is <see langword="false"/>.
    /// </summary>
    /// <param name="logger">The worker's logger.</param>
    /// <param name="finalized">Whether the statement still held the claim.</param>
    /// <param name="queue">A short name for the queue, for the message (<c>"delivery"</c>).</param>
    /// <param name="id">The row's id.</param>
    /// <returns><paramref name="finalized"/>.</returns>
    public static bool LogFinalizeOutcome(this ILogger logger, bool finalized, string queue, Guid id) {
        ArgumentNullException.ThrowIfNull(logger);
        if (!finalized) ClaimLost(logger, queue, id, null);
        return finalized;
    }
}
