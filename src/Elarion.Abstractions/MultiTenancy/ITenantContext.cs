namespace Elarion.Abstractions.MultiTenancy;

/// <summary>
/// The ambient tenant the current unit of work belongs to. A scoped service: every read filter and every
/// insert stamp in the scope answers to this one value, so per-tenant isolation is a property of the scope
/// rather than something each query and each <c>Add</c> has to remember.
/// </summary>
/// <remarks>
/// <para>
/// The value is produced by the <see cref="ITenantResolver"/> seam — a claim by default, a membership lookup
/// or anything else in an application that replaces it — and is resolved at most once per scope. It can also
/// be set explicitly with <see cref="Scope"/>, which is what an application does when its own resolution is
/// asynchronous (a database lookup cannot run inside a query filter) or when a background worker iterates
/// tenants.
/// </para>
/// <para>
/// <b>Unresolved fails closed.</b> A <see langword="null"/> <see cref="TenantId"/> outside a system scope
/// matches no rows on read and throws on write, rather than silently reading or writing tenant-less data.
/// </para>
/// </remarks>
public interface ITenantContext {
    /// <summary>
    /// The current tenant's id as text, or <see langword="null"/> when none is resolved. Keys of other shapes
    /// (<see cref="System.Guid"/>, <see cref="int"/>, <see cref="long"/>) are converted per entity, so this one
    /// contract serves every tenant key type.
    /// </summary>
    string? TenantId { get; }

    /// <summary>
    /// Whether the scope is currently declared to span every tenant. See <see cref="SystemScope"/>.
    /// </summary>
    bool IsSystemScope { get; }

    /// <summary>
    /// Declares that the work inside spans <b>every</b> tenant: read filters are satisfied unconditionally and
    /// writes are not stamped or checked. Disposing the returned handle restores the previous state, and nested
    /// scopes are honoured.
    /// </summary>
    /// <remarks>
    /// This exists so "spans all tenants" is something a reviewer can <i>see</i>. Without it the only way out of
    /// a per-call-site filter is to not write the call — textually indistinguishable from having forgotten it.
    /// Restrict it to work that genuinely has no tenant: a cross-tenant maintenance job, a migration, an
    /// operator report.
    /// </remarks>
    /// <example>
    /// <code>
    /// public sealed class PurgeExpired(AppDbContext db, ITenantContext tenant) {
    ///     [ScheduledJob("sessions.purgeExpired", Cron = "0 3 * * *")]
    ///     public async Task RunAsync(CancellationToken ct) {
    ///         using var _ = tenant.SystemScope();          // deliberately every tenant
    ///         await db.Sessions.Where(s =&gt; s.ExpiresAt &lt; now).ExecuteDeleteAsync(ct);
    ///     }
    /// }
    /// </code>
    /// </example>
    /// <returns>A handle that restores the previous scope state when disposed.</returns>
    IDisposable SystemScope();

    /// <summary>
    /// Enters <paramref name="tenantId"/> explicitly, overriding whatever the resolver would produce. This is
    /// the entry point for asynchronous resolution (resolve first, then enter the scope) and for a worker that
    /// processes one tenant at a time. Disposing the returned handle restores the previous tenant.
    /// </summary>
    /// <param name="tenantId">The tenant to enter. Must not be empty.</param>
    /// <returns>A handle that restores the previous tenant when disposed.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="tenantId"/> is empty or whitespace.</exception>
    IDisposable Scope(string tenantId);
}
