namespace Elarion.Abstractions.MultiTenancy;

/// <summary>
/// Marks an entity as belonging to exactly one tenant. Every row of a tenant-scoped entity is owned by the
/// tenant in <see cref="TenantId"/>, and cross-tenant visibility is never legitimate — which is what separates
/// tenancy from <i>sharing</i> (<c>[ResourceFilter]</c>, where a row some users may see is an opt-in per-query
/// decision).
/// </summary>
/// <remarks>
/// <para>
/// The marker is the whole declaration: <c>ApplyElarionTenantScoping</c> discovers every implementing entity in
/// the model and attaches both legs — the read filter (a named EF Core query filter) and the write stamp (a
/// <c>SaveChanges</c> interceptor) — so neither can be forgotten at a call site. See the
/// <see href="https://elarion.wimmesberger.dev/docs/capabilities/multi-tenancy">multi-tenancy capability</see>.
/// </para>
/// <para>
/// <typeparamref name="TTenantId"/> may be <see cref="System.Guid"/>, <see cref="string"/>, <see cref="int"/>,
/// or <see cref="long"/> — the same key types <c>[ResourceFilter]</c>'s tenant rule accepts. The property must
/// be settable: the framework stamps it on insert, and a hand-set value is verified rather than trusted.
/// </para>
/// </remarks>
/// <typeparam name="TTenantId">The tenant key type.</typeparam>
/// <example>
/// <code>
/// public sealed class Contact : ITenantScoped&lt;Guid&gt; {
///     public Guid Id { get; init; } = Guid.CreateVersion7();
///     public Guid TenantId { get; set; }          // stamped on insert, filtered on read
///     public required string Name { get; set; }
/// }
/// </code>
/// </example>
public interface ITenantScoped<TTenantId>
    where TTenantId : notnull {
    /// <summary>
    /// The owning tenant. Stamped from the ambient <see cref="ITenantContext"/> when the row is inserted, and
    /// required to match it on every read, update, and delete outside a declared
    /// <see cref="ITenantContext.SystemScope">system scope</see>.
    /// </summary>
    TTenantId TenantId { get; set; }
}
