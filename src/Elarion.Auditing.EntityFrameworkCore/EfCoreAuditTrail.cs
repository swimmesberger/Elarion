using System.Text.Json;
using Elarion.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// The durable <see cref="IAuditTrail"/> (ADR-0045), writing <see cref="AuditLogEntry"/> rows through the
/// application's own <typeparamref name="TDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RecordAsync"/> is the success path: with the caller's unit-of-work transaction ambient on the
/// scoped context, the entry is a tracked <c>Add</c> that the transaction decorator's finalizing flush persists
/// — the record commits (or rolls back) atomically with the business writes, never on its own. With no ambient
/// transaction (queries/read auditing) the entry is saved immediately; note that flush also persists any other
/// pending changes on the context, which for a transaction-less handler is the intended "audit rides the same
/// flush" semantics.
/// </para>
/// <para>
/// <see cref="RecordDetachedAsync"/> is the denial/failure path: it writes through a <b>fresh</b> DI scope (its
/// own context and connection), so the record survives the caller's rollback. That fresh scope's audit scope is
/// inactive, so the write never re-enters change capture.
/// </para>
/// </remarks>
public sealed class EfCoreAuditTrail<TDbContext>(
    TDbContext dbContext,
    IServiceScopeFactory scopeFactory
) : IAuditTrail
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async ValueTask RecordAsync(Func<AuditRecord> buildRecord, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(buildRecord);

        // Flush the handler's pending writes FIRST: the common shape leaves them for the unit-of-work commit
        // flush, which would run only after the record materialized — too late for the change-capture
        // interceptor to contribute their diffs. Inside the ambient transaction this flush is atomic with the
        // commit anyway; with no transaction it is exactly the flush that persists the handler's work.
        var inTransaction = dbContext.Database.CurrentTransaction is not null;
        if (dbContext.ChangeTracker.HasChanges()) {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        dbContext.Add(ToEntry(buildRecord()));
        if (!inTransaction) {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask RecordDetachedAsync(AuditRecord record, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(record);

        await using var scope = scopeFactory.CreateAsyncScope();
        var detached = scope.ServiceProvider.GetRequiredService<TDbContext>();
        detached.Add(ToEntry(record));
        await detached.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AuditLogEntry ToEntry(AuditRecord record) => new() {
        Id = record.Id,
        OccurredAtUtc = record.OccurredAt,
        Action = record.Action,
        Module = record.Module,
        UserId = record.UserId,
        ResourceType = record.ResourceType,
        ResourceId = record.ResourceId,
        ParentResourceType = record.ParentResourceType,
        ParentResourceId = record.ParentResourceId,
        Outcome = record.Outcome.ToString(),
        ErrorKind = record.ErrorKind,
        CorrelationId = record.CorrelationId,
        Changes = record.Changes.Count > 0
            ? JsonSerializer.Serialize(ToArray(record.Changes), AuditingJsonContext.Default.AuditChangeArray)
            : null,
        Details = record.Details.Count > 0
            ? JsonSerializer.Serialize(
                record.Details as Dictionary<string, string> ?? new Dictionary<string, string>(record.Details),
                AuditingJsonContext.Default.DictionaryStringString)
            : null,
    };

    private static AuditChange[] ToArray(IReadOnlyList<AuditChange> changes) =>
        changes as AuditChange[] ?? [.. changes];
}
