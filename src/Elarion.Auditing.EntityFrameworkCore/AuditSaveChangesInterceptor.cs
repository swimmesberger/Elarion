using System.Data.Common;
using Elarion.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Runs every registered <see cref="IAuditChangeContributor"/> at each flush of an audited invocation
/// (ADR-0045). Capture happens per <c>SavingChanges</c> — not once before commit — because a handler may flush
/// mid-flight and every flush resets the change tracker's original values; the interceptor is the only point
/// that sees all of them. Scoped, so it shares the invocation's <see cref="AuditScope"/>; attached to the
/// context automatically via <c>IDbContextOptionsConfiguration</c> (the settings-dispatch pattern).
/// </summary>
/// <remarks>
/// Also the sink's <b>durability callback</b>: when <see cref="EfCoreAuditTrail{TDbContext}.RecordAsync"/>
/// enlisted the success record in the ambient transaction, the scope holds a pending mark that this
/// interceptor promotes to <see cref="AuditScope.Recorded"/> from <c>TransactionCommitted</c> — the moment
/// the record actually became durable. A commit-phase failure never fires the callback, so the scope stays
/// unrecorded and the outer audit decorator writes the detached failure record instead.
/// </remarks>
public sealed class AuditSaveChangesInterceptor(
    AuditScope scope,
    IEnumerable<IAuditChangeContributor> contributors
) : SaveChangesInterceptor, IDbTransactionInterceptor {
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result) {
        Capture(eventData, saved: false);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) {
        Capture(eventData, saved: false);
        return ValueTask.FromResult(result);
    }

    /// <inheritdoc />
    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result) {
        Capture(eventData, saved: true);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) {
        Capture(eventData, saved: true);
        return ValueTask.FromResult(result);
    }

    /// <summary>Promotes the pending success record to durably recorded — the transaction just committed.</summary>
    void IDbTransactionInterceptor.TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) =>
        PromotePendingRecord();

    /// <inheritdoc cref="IDbTransactionInterceptor.TransactionCommitted" />
    Task IDbTransactionInterceptor.TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken) {
        PromotePendingRecord();
        return Task.CompletedTask;
    }

    private void PromotePendingRecord() {
        if (scope.RecordPending)
            scope.MarkRecorded();
    }

    private void Capture(DbContextEventData eventData, bool saved) {
        if (!scope.IsActive || eventData.Context is not { } context)
            return;

        var captureContext = new AuditCaptureContext { DbContext = context, Scope = scope };
        foreach (var contributor in contributors) {
            if (saved) {
                contributor.OnSavedChanges(captureContext);
            } else {
                contributor.OnSavingChanges(captureContext);
            }
        }
    }
}
