namespace Elarion.Paging;

/// <summary>
/// Declares a data-level authorization filter for <typeparamref name="TEntity"/> on the annotated partial
/// class. The source generator fills the class with a strongly-typed, reflection-free
/// <c>IQueryAuthorizer&lt;TEntity&gt;</c> implementation plus a static <c>Specification</c> singleton, so a
/// handler restricts a list query to the rows the caller may see with
/// <c>source.WhereAuthorized(MyAccess.Specification, currentUser)</c> before paging.
/// </summary>
/// <remarks>
/// Declare one partial class per entity, near the feature that lists it — the entity stays free of
/// authorization concerns (mirroring <c>[Keyset&lt;TEntity&gt;]</c>). The generated predicate composes the
/// declared rules as <c>AND(scope rules) AND OR(grant rules)</c>: a row is visible when it satisfies every
/// scope (e.g. <see cref="TenantProperty"/>) and at least one grant (e.g. <see cref="OwnerProperty"/>).
/// At least one rule must be declared.
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
    where TEntity : class
{
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
