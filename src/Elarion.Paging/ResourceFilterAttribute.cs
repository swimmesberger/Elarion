namespace Elarion.Paging;

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
    public string? OwnerProperty { get; set; }

    /// <summary>
    /// The entity property holding the tenant id (a <b>scope</b> rule, AND-combined): every accessible row
    /// must have this column equal to the current principal's tenant claim (see <see cref="TenantClaimType"/>).
    /// The property type may be <see cref="System.Guid"/>, <see cref="string"/>, <see cref="int"/>, or <see cref="long"/>.
    /// </summary>
    public string? TenantProperty { get; set; }

    /// <summary>
    /// The claim type the tenant value is read from when <see cref="TenantProperty"/> is set. Defaults to
    /// <c>"tenant"</c>.
    /// </summary>
    public string TenantClaimType { get; set; } = "tenant";

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
    public bool Shared { get; set; }

    /// <summary>
    /// The resource type discriminator stored in the grants table (e.g. <c>"Contact"</c>), used by the
    /// <see cref="Shared"/> rule's <c>EXISTS</c>. Required when <see cref="Shared"/> is <see langword="true"/>.
    /// </summary>
    public string? ResourceTypeName { get; set; }

    /// <summary>
    /// The entity key property whose stringified value is matched against the grants table's resource id for the
    /// <see cref="Shared"/> rule. Defaults to <c>"Id"</c>. The property type may be
    /// <see cref="System.Guid"/>, <see cref="string"/>, <see cref="int"/>, or <see cref="long"/>.
    /// </summary>
    public string IdProperty { get; set; } = "Id";
}
