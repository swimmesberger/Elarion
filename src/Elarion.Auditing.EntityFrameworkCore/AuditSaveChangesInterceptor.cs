using Elarion.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Runs every registered <see cref="IAuditChangeContributor"/> at each flush of an audited invocation
/// (ADR-0045). Capture happens per <c>SavingChanges</c> — not once before commit — because a handler may flush
/// mid-flight and every flush resets the change tracker's original values; the interceptor is the only point
/// that sees all of them. Scoped, so it shares the invocation's <see cref="IAuditScope"/>; attached to the
/// context automatically via <c>IDbContextOptionsConfiguration</c> (the settings-dispatch pattern).
/// </summary>
public sealed class AuditSaveChangesInterceptor(
    IAuditScope scope,
    IEnumerable<IAuditChangeContributor> contributors
) : SaveChangesInterceptor {
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
