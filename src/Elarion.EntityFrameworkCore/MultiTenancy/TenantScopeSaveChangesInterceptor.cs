using Elarion.Abstractions.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Elarion.EntityFrameworkCore.MultiTenancy;

/// <summary>
/// The write leg of ambient tenant scoping: stamps the tenant onto inserted rows and refuses any write that
/// would cross a tenant boundary.
/// </summary>
/// <remarks>
/// <para>
/// The read filter alone leaves the other half of the isolation open. Without a write leg every
/// <c>db.X.Add(...)</c> has to remember the tenant id, and forgetting writes the row into the wrong tenant —
/// or into none, where it fails the read filter forever and becomes invisible to the person who created it.
/// Stamping at the one point every write passes through closes that class of bug the same way the filter closes
/// the read one.
/// </para>
/// <para>
/// Which entities are tenant-scoped, and which property carries the key, is read from the
/// <see cref="ElarionTenantScoping.TenantPropertyAnnotation">model annotation</see> that
/// <c>ApplyElarionTenantScoping</c> wrote — so the check costs a metadata lookup per changed entry and no
/// reflection. An entity that is tenant-scoped but whose context never applied the model pass carries no
/// annotation and is left alone; that combination is a configuration error the model pass itself reports.
/// </para>
/// <para>
/// <b>What this does not cover.</b> <c>ExecuteUpdate</c>/<c>ExecuteDelete</c> and raw SQL do not go through
/// <c>SaveChanges</c>, so they are not stamped or checked — though the former two still run against the
/// filtered query, so they cannot reach another tenant's rows. Bulk-copy inserts bypass both legs and must set
/// the tenant themselves.
/// </para>
/// </remarks>
/// <param name="tenantContext">The tenant in scope for this unit of work.</param>
public sealed class TenantScopeSaveChangesInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor {
    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result) {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default) {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context) {
        if (context is null) return;

        // Work that legitimately spans tenants declares it once, and then owns the tenant ids it writes.
        if (tenantContext.IsSystemScope) return;

        var tenantId = tenantContext.TenantId;

        foreach (var entry in context.ChangeTracker.Entries()) {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            if (entry.Metadata.FindAnnotation(ElarionTenantScoping.TenantPropertyAnnotation)?.Value is not
                string propertyName) continue;

            var property = entry.Property(propertyName);
            var expected = ElarionTenantScoping.Convert(tenantId, property.Metadata.ClrType);
            if (expected is null)
                throw new TenantScopeViolationException(
                    $"Cannot {Describe(entry.State)} '{entry.Metadata.ClrType.Name}': no tenant is in scope"
                    + (tenantId is null
                        ? ". Enter one with ITenantContext.Scope(tenantId), or declare ITenantContext"
                          + ".SystemScope() for work that spans every tenant."
                        : $" that '{tenantId}' can be read as a {property.Metadata.ClrType.Name} tenant key."));

            if (entry.State == EntityState.Added) {
                StampInsert(entry, property, expected);
                continue;
            }

            GuardExistingRow(entry, property, expected);
        }
    }

    /// <summary>
    /// Stamps an inserted row, or verifies a tenant the caller set explicitly. A caller-set value is checked
    /// rather than trusted, so a handler cannot write into another tenant by assigning the key by hand.
    /// </summary>
    private static void StampInsert(EntityEntry entry, PropertyEntry property, object expected) {
        var current = property.CurrentValue;
        if (current is null || IsDefault(current, property.Metadata.ClrType)) {
            property.CurrentValue = expected;
            return;
        }

        if (!expected.Equals(current))
            throw new TenantScopeViolationException(
                $"Cannot insert '{entry.Metadata.ClrType.Name}' with tenant '{current}' while tenant "
                + $"'{expected}' is in scope. Enter the target tenant with ITenantContext.Scope(tenantId) "
                + "rather than assigning the tenant key directly.");
    }

    /// <summary>
    /// Refuses an update or delete that reaches outside the current tenant, and refuses moving a row between
    /// tenants. The original value is what the row actually had, so this holds even for an entity attached
    /// without being loaded through the filtered query.
    /// </summary>
    private static void GuardExistingRow(EntityEntry entry, PropertyEntry property, object expected) {
        var original = property.OriginalValue;
        if (original is not null && !IsDefault(original, property.Metadata.ClrType) && !expected.Equals(original))
            throw new TenantScopeViolationException(
                $"Cannot {Describe(entry.State)} '{entry.Metadata.ClrType.Name}' belonging to tenant "
                + $"'{original}' while tenant '{expected}' is in scope.");

        if (entry.State == EntityState.Modified && property.IsModified)
            throw new TenantScopeViolationException(
                $"Cannot move '{entry.Metadata.ClrType.Name}' from tenant '{property.OriginalValue}' to "
                + $"'{property.CurrentValue}'. A row belongs to exactly one tenant for its whole life; copy it "
                + "into the target tenant instead.");
    }

    private static bool IsDefault(object value, Type clrType) {
        if (clrType == typeof(Guid)) return (Guid)value == Guid.Empty;
        if (clrType == typeof(string)) return ((string)value).Length == 0;
        if (clrType == typeof(int)) return (int)value == 0;
        if (clrType == typeof(long)) return (long)value == 0L;

        return false;
    }

    private static string Describe(EntityState state) {
        return state switch {
            EntityState.Added => "insert",
            EntityState.Modified => "update",
            _ => "delete"
        };
    }
}
