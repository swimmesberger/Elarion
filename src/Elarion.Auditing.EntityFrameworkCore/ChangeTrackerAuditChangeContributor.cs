using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Elarion.Abstractions.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// The default <see cref="IAuditChangeContributor"/>: diffs the change tracker for entities opted in with
/// <c>[Audited]</c>. Modified entities contribute one <see cref="AuditChange"/> per actually-changed property
/// (old → new, skipping <c>[AuditIgnore]</c> and primary-key columns), captured at saving time while original
/// values are intact. Deletions are captured at saving time too; additions are deferred to
/// <see cref="OnSavedChanges"/> so store-generated keys are final. Values are invariant-formatted strings.
/// </summary>
/// <remarks>
/// Capture is opt-in per entity on purpose (fail-closed PII stance, and it keeps framework tables — outbox,
/// idempotency, the audit log itself — out of capture; see ADR-0045). Writes that bypass the change tracker
/// (<c>ExecuteUpdate</c>/<c>ExecuteDelete</c>/raw SQL) are invisible here — the handler records those through
/// <see cref="IAuditScope.AddChange"/>.
/// </remarks>
public sealed class ChangeTrackerAuditChangeContributor : IAuditChangeContributor {
    // Per-CLR-type capture policy, computed once per process: whether the type is [Audited] and which property
    // names are [AuditIgnore]d. Plain attribute reflection on a known Type, cached — never per-flush.
    private static readonly ConcurrentDictionary<Type, CapturePolicy?> Policies = new();

    // Added entities observed at saving time, recorded after the flush so store-generated keys are final.
    // Scoped state: cleared at the start of every capture so a failed earlier flush leaves no stale entries.
    private readonly List<(EntityEntry Entry, string EntityName)> _pendingAdds = [];

    /// <inheritdoc />
    public void OnSavingChanges(AuditCaptureContext context) {
        _pendingAdds.Clear();

        foreach (var entry in context.DbContext.ChangeTracker.Entries()) {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var policy = Policies.GetOrAdd(entry.Entity.GetType(), BuildPolicy);
            if (policy is null)
                continue;

            switch (entry.State) {
                case EntityState.Added:
                    _pendingAdds.Add((entry, policy.EntityName));
                    break;
                case EntityState.Deleted:
                    context.Scope.AddChange(new AuditChange {
                        Entity = policy.EntityName,
                        EntityId = FormatKey(entry, original: true),
                        Kind = AuditChangeKind.Deleted,
                    });
                    break;
                case EntityState.Modified:
                    ContributeModified(context.Scope, entry, policy);
                    break;
            }
        }
    }

    /// <inheritdoc />
    public void OnSavedChanges(AuditCaptureContext context) {
        foreach (var (entry, entityName) in _pendingAdds) {
            context.Scope.AddChange(new AuditChange {
                Entity = entityName,
                EntityId = FormatKey(entry, original: false),
                Kind = AuditChangeKind.Added,
            });
        }

        _pendingAdds.Clear();
    }

    private static void ContributeModified(IAuditScope scope, EntityEntry entry, CapturePolicy policy) {
        var entityId = FormatKey(entry, original: false);
        foreach (var property in entry.Properties) {
            if (!property.IsModified || property.Metadata.IsPrimaryKey())
                continue;

            if (policy.IgnoredProperties.Contains(property.Metadata.Name))
                continue;

            // IsModified is a marker, not a comparison — an unchanged value re-assigned still flags it. Only an
            // actual difference is an auditable change.
            if (Equals(property.OriginalValue, property.CurrentValue))
                continue;

            scope.AddChange(new AuditChange {
                Entity = policy.EntityName,
                EntityId = entityId,
                Property = property.Metadata.Name,
                OldValue = FormatValue(property.OriginalValue),
                NewValue = FormatValue(property.CurrentValue),
                Kind = AuditChangeKind.Modified,
            });
        }
    }

    private static string? FormatKey(EntityEntry entry, bool original) {
        var key = entry.Metadata.FindPrimaryKey();
        if (key is null)
            return null;

        string? result = null;
        foreach (var keyProperty in key.Properties) {
            var propertyEntry = entry.Property(keyProperty.Name);
            var value = FormatValue(original ? propertyEntry.OriginalValue : propertyEntry.CurrentValue);
            result = result is null ? value : $"{result}/{value}";
        }

        return result;
    }

    private static string? FormatValue(object? value) => value switch {
        null => null,
        string s => s,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static CapturePolicy? BuildPolicy(Type entityType) {
        if (entityType.GetCustomAttribute<AuditedAttribute>(inherit: false) is null)
            return null;

        HashSet<string>? ignored = null;
        foreach (var property in entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (property.GetCustomAttribute<AuditIgnoreAttribute>(inherit: false) is not null) {
                ignored ??= new HashSet<string>(StringComparer.Ordinal);
                ignored.Add(property.Name);
            }
        }

        return new CapturePolicy(entityType.Name, ignored ?? EmptyIgnored);
    }

    private static readonly HashSet<string> EmptyIgnored = [];

    private sealed record CapturePolicy(string EntityName, HashSet<string> IgnoredProperties);
}
