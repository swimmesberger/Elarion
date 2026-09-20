using System.Globalization;
using Elarion.Abstractions.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace Elarion.EntityFrameworkCore.MultiTenancy;

/// <summary>
/// The names and conversions shared by the two legs of ambient tenant scoping — the model-level read filter
/// and the <c>SaveChanges</c> stamp — plus the accessors the generated filter expression calls at query time.
/// </summary>
/// <remarks>
/// The <c>Current*</c> and <c>As*</c> members are part of the emitted query-filter expression rather than an
/// API to call by hand. They are public because Entity Framework Core evaluates the filter expression against
/// the live context on every query, and an expression cannot call an inaccessible method.
/// </remarks>
public static class ElarionTenantScoping {
    /// <summary>
    /// The key of the named query filter this feature attaches, so a query can drop <b>only</b> the tenant
    /// filter with <c>IgnoreQueryFilters([ElarionTenantScoping.QueryFilterKey])</c> and keep the application's
    /// own filters in place.
    /// </summary>
    /// <remarks>
    /// Prefer <see cref="ITenantContext.SystemScope"/> for work that spans tenants: it says so once for a whole
    /// unit of work, covers the write leg too, and reads as a declaration rather than as an exception to one
    /// query.
    /// </remarks>
    public const string QueryFilterKey = "Elarion:TenantScope";

    /// <summary>
    /// The entity-type annotation recording which property carries the tenant id. Written by
    /// <c>ApplyElarionTenantScoping</c> and read by the stamping interceptor, so the write leg needs no
    /// reflection at runtime — the model already knows.
    /// </summary>
    public const string TenantPropertyAnnotation = "Elarion:TenantProperty";

    /// <summary>
    /// The tenant in scope for <paramref name="context"/>, or <see langword="null"/> when none is resolved.
    /// Called from the generated query filter; Entity Framework Core substitutes the executing context.
    /// </summary>
    /// <param name="context">The context the query is running on.</param>
    public static string? CurrentTenantId(DbContext context) {
        ArgumentNullException.ThrowIfNull(context);
        return TenantScopeOptionsExtension.FindTenantContext(context)?.TenantId;
    }

    /// <summary>
    /// Whether <paramref name="context"/> is running inside a declared system scope, in which case the tenant
    /// filter is satisfied unconditionally. Called from the generated query filter.
    /// </summary>
    /// <param name="context">The context the query is running on.</param>
    public static bool IsSystemScope(DbContext context) {
        ArgumentNullException.ThrowIfNull(context);
        return TenantScopeOptionsExtension.FindTenantContext(context)?.IsSystemScope ?? false;
    }

    /// <summary>Converts the canonical tenant id to a <see cref="Guid"/> key, or <see langword="null"/>.</summary>
    /// <param name="tenantId">The tenant id as text.</param>
    public static Guid? AsGuid(string? tenantId) {
        return Guid.TryParse(tenantId, out var value) ? value : null;
    }

    /// <summary>Converts the canonical tenant id to an <see cref="int"/> key, or <see langword="null"/>.</summary>
    /// <param name="tenantId">The tenant id as text.</param>
    public static int? AsInt32(string? tenantId) {
        return int.TryParse(tenantId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>Converts the canonical tenant id to a <see cref="long"/> key, or <see langword="null"/>.</summary>
    /// <param name="tenantId">The tenant id as text.</param>
    public static long? AsInt64(string? tenantId) {
        return long.TryParse(tenantId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// Returns the canonical tenant id unchanged for a <see cref="string"/> key, normalizing blank to
    /// <see langword="null"/> so an empty tenant never matches a row.
    /// </summary>
    /// <param name="tenantId">The tenant id as text.</param>
    public static string? AsString(string? tenantId) {
        return string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
    }

    /// <summary>
    /// Converts the canonical tenant id to the CLR type of a tenant key property, for the write leg.
    /// </summary>
    /// <param name="tenantId">The tenant id as text.</param>
    /// <param name="keyType">The tenant property's CLR type.</param>
    /// <returns>The converted key, or <see langword="null"/> when the id is absent or malformed.</returns>
    internal static object? Convert(string? tenantId, Type keyType) {
        if (keyType == typeof(Guid)) return AsGuid(tenantId);
        if (keyType == typeof(string)) return AsString(tenantId);
        if (keyType == typeof(int)) return AsInt32(tenantId);
        if (keyType == typeof(long)) return AsInt64(tenantId);

        return null;
    }
}
