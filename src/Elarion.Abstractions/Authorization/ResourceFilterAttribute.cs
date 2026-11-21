namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Declares a data-level authorization filter for <typeparamref name="TEntity"/> on the annotated partial
/// class. The source generator fills the class with a strongly-typed, reflection-free
/// <c>IQueryAuthorizer&lt;TEntity&gt;</c> implementation and auto-registers it, so a handler restricts a list
/// query to the rows the caller may see with <c>source.WhereAuthorized(authorizer, currentUser)</c> before paging.
/// </summary>
/// <remarks>
/// <para>
/// Declare one partial class per entity, near the feature that lists it — the entity stays free of
/// authorization concerns (mirroring <c>[Keyset&lt;TEntity&gt;]</c>). The generated predicate composes the
/// declared rules as <c>AND(scope rules) AND OR(grant rules)</c>: a row is visible when it satisfies every
/// scope (e.g. <see cref="TenantProperty"/>) and at least one grant (e.g. <see cref="OwnerProperty"/>).
/// At least one rule must be declared.
/// </para>
/// <para>
/// <b>Consumption.</b> A filter with only field rules (<see cref="OwnerProperty"/>/<see cref="TenantProperty"/>)
/// is stateless, so the generator also exposes a static <c>Specification</c> singleton you can pass to
/// <c>WhereAuthorized</c> without DI. A filter that sets <see cref="Shared"/> consults the grants table, so it
/// is a <b>scoped service</b> with no static accessor — inject <c>IQueryAuthorizer&lt;TEntity&gt;</c> instead.
/// Both forms are registered as <c>IQueryAuthorizer&lt;TEntity&gt;</c>, so constructor injection works either way.
/// </para>
/// <para>
/// <b>Composition.</b> A rule the properties cannot express is composed around the generated filter with
/// <see cref="QueryAuthorizers"/>; mark the filter <see cref="Register"/> <c>= false</c> and register the composed
/// authorizer, so there is exactly one <c>IQueryAuthorizer&lt;TEntity&gt;</c> and it is the whole rule.
/// </para>
/// </remarks>
/// <typeparam name="TEntity">The entity type being authorized.</typeparam>
/// <example>
/// <code>
/// [ResourceFilter&lt;Contact&gt;(OwnerProperty = nameof(Contact.OwnerId), TenantProperty = nameof(Contact.TenantId))]
/// public sealed partial class ContactAccess;
///
/// // handler
/// var page = await db.Contacts
///     .WhereAuthorized(ContactAccess.Specification, currentUser)
///     .ToKeysetPageAsync(request, RecentContacts.Definition, c =&gt; new ContactDto(c.Id));
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ResourceFilterAttribute<TEntity> : Attribute
    where TEntity : class {
    /// <summary>
    /// The entity property holding the owning principal's id (a <b>grant</b> rule, OR-combined): a row is
    /// accessible when this column equals the current principal's <c>UserId</c>. The property type may be
    /// <see cref="System.Guid"/>, <see cref="string"/>, <see cref="int"/>, or <see cref="long"/>.
    /// </summary>
    public string? OwnerProperty { get; init; }

    /// <summary>
    /// The entity property holding the tenant id (a <b>scope</b> rule, AND-combined): every accessible row
    /// must have this column equal to the current principal's tenant claim (see <see cref="TenantClaimType"/>).
    /// The property type may be <see cref="System.Guid"/>, <see cref="string"/>, <see cref="int"/>, or <see cref="long"/>.
    /// </summary>
    public string? TenantProperty { get; init; }

    /// <summary>
    /// The claim type the tenant value is read from when <see cref="TenantProperty"/> is set. Defaults to
    /// <c>"tenant"</c>.
    /// </summary>
    public string TenantClaimType { get; init; } = "tenant";

    /// <summary>
    /// Enables a <b>shared-grant</b> rule (OR-combined with <see cref="OwnerProperty"/>): a row is also
    /// accessible when the resource-grants table records a share with the current principal — the user, or any
    /// of their roles — for the operation. Emitted as an indexed correlated <c>EXISTS</c> over the grants table,
    /// so role sharing is filtered in the database. Requires <see cref="ResourceTypeName"/>, and the owning
    /// assembly to reference <c>Elarion.Authorization.EntityFrameworkCore</c>.
    /// </summary>
    /// <remarks>
    /// The matched operation is the one passed to <c>WhereAuthorized</c>/<c>IQueryAuthorizer.GetFilter</c>,
    /// which <b>defaults to <c>Read</c></b>, so the same filter lists read- or write-shared rows depending on
    /// the operation argument; the scope/owner rules are operation-independent. Enabling this makes the
    /// generated filter a <b>scoped service</b> (it injects the grants source), so consume it via an injected
    /// <c>IQueryAuthorizer&lt;TEntity&gt;</c> rather than a static <c>Specification</c>.
    /// </remarks>
    public bool Shared { get; init; }

    /// <summary>
    /// The resource type discriminator stored in the grants table (e.g. <c>"Contact"</c>), used by the
    /// <see cref="Shared"/> rule's <c>EXISTS</c>. Required when <see cref="Shared"/> is <see langword="true"/>.
    /// </summary>
    public string? ResourceTypeName { get; init; }

    /// <summary>
    /// The entity key property whose stringified value is matched against the grants table's resource id for the
    /// <see cref="Shared"/> rule. Defaults to <c>"Id"</c>. The property type may be
    /// <see cref="System.Guid"/>, <see cref="string"/>, <see cref="int"/>, or <see cref="long"/>.
    /// </summary>
    public string IdProperty { get; init; } = "Id";

    /// <summary>
    /// A permission whose holders see every row <b>of their scope</b> — the "holds <c>contacts.manage</c>, sees all"
    /// rule: for such a caller the grant rules (<see cref="OwnerProperty"/>, <see cref="Shared"/>) are lifted, while the
    /// <see cref="TenantProperty"/> scope still applies (and still fails closed without a tenant claim). Only a filter
    /// without a tenant rule becomes unrestricted (<see langword="null"/>). Matched against the caller's
    /// <see cref="PermissionClaimType"/> claims (a permission is <c>"{resource}.{verb}"</c>, as
    /// <c>[RequirePermission]</c> spells it). <see langword="null"/> grants no bypass.
    /// </summary>
    /// <remarks>
    /// The bypass is part of this filter, so everything that reads the filter sees it: the list query, and the
    /// <c>[RequireResource]</c> point check once the entity's filter check is registered
    /// (<c>AddElarionResourceFilterCheck</c>). The grants-only point check knows neither the owner column nor this
    /// permission.
    /// </remarks>
    public string? BypassPermission { get; init; }

    /// <summary>
    /// The claim type <see cref="BypassPermission"/> is read from. Defaults to <c>"permission"</c>, the default of
    /// <see cref="AuthorizationOptions.PermissionClaimType"/>; set the same value when the host changes it.
    /// </summary>
    public string PermissionClaimType { get; init; } = AuthorizationOptions.DefaultPermissionClaimType;

    /// <summary>
    /// A role whose members see every row <b>of their scope</b> — <see cref="BypassPermission"/> for hosts whose
    /// administrators are a role rather than a permission. The same rules apply: the grant rules are lifted, the
    /// <see cref="TenantProperty"/> scope stays. With both set, either one bypasses. <see langword="null"/> grants no
    /// bypass.
    /// </summary>
    public string? BypassRole { get; init; }

    /// <summary>
    /// Whether the host bootstrapper registers the generated filter as <c>IQueryAuthorizer&lt;TEntity&gt;</c>.
    /// Defaults to <see langword="true"/>. Set it to <see langword="false"/> on a filter that is only a <b>building
    /// block</b> of a composed rule (<see cref="QueryAuthorizers.AnyOf{TEntity}"/>), and register the composition
    /// instead — otherwise the building block is what an injected <c>IQueryAuthorizer&lt;TEntity&gt;</c> and the
    /// filter-backed <c>[RequireResource]</c> check resolve, which is only part of the rule.
    /// </summary>
    /// <remarks>
    /// The generated registration only fills a gap (<c>TryAdd</c>): an <c>IQueryAuthorizer&lt;TEntity&gt;</c> the
    /// application registers itself — in a module's <c>ConfigureServices</c>, or in the host before or after
    /// <c>AddElarion</c> — is the one that resolves. <see cref="Register"/> <c>= false</c> says so at the declaration,
    /// and is required when two filters are declared for one entity (ELMOD007).
    /// </remarks>
    public bool Register { get; init; } = true;
}
