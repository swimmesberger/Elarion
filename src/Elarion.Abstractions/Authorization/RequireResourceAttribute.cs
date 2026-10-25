namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Requires the current principal to be authorized for a specific <b>resource</b> the request names — the
/// per-resource (BOLA) point check, enforced in the handler pipeline like the other <c>[Require*]</c>
/// attributes. The resource id is read from the request by a compile-checked path (no string expression
/// language): per <see href="../../docs/decisions/0012-dynamic-variable-references.md">ADR-0012</see>, an
/// attribute <i>references</i> a value by path and never computes — the generator validates the path against
/// the request type and emits a zero-reflection typed accessor.
/// </summary>
/// <example>
/// <code>
/// [RequireResource(typeof(Contact), Operation = "read", Id = nameof(GetContactQuery.Id))]
/// public sealed class GetContactQuery : IQuery { public Guid Id { get; init; } }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class RequireResourceAttribute(Type resourceType) : Attribute {
    /// <summary>The resource type being accessed (e.g. <c>typeof(Contact)</c>).</summary>
    public Type ResourceType { get; } = resourceType;

    /// <summary>
    /// An explicit resource-type discriminator matched against the grants table, overriding the default derived
    /// from <see cref="ResourceType"/> (its <see cref="System.Type.FullName"/>). Set this only when a stable,
    /// namespace-independent discriminator is required, and set the <b>same</b> string on the corresponding
    /// <c>[ResourceFilter].ResourceTypeName</c> and on every <c>IResourceGrantStore</c> grant for the resource —
    /// all three paths must agree. Compared with <see cref="System.StringComparison.Ordinal"/>.
    /// </summary>
    public string? ResourceTypeName { get; init; }

    /// <summary>The operation name (an open <see cref="ResourceOperation"/> value). Defaults to <c>"read"</c>.</summary>
    public string Operation { get; init; } = "read";

    /// <summary>
    /// The request property path identifying the resource id, as a compile-checked path — typically
    /// <c>nameof(Req.Id)</c>, or a dotted path (<c>nameof(Req.Customer) + "." + nameof(Customer.Id)</c>).
    /// Defaults to <c>"Id"</c>. A path that names no such member is a generator diagnostic (ELAUTH002).
    /// </summary>
    public string Id { get; init; } = "Id";

    /// <summary>
    /// How a denied caller is answered. <see cref="ResourceDenial.Forbidden"/> (the default) answers <c>403</c>;
    /// <see cref="ResourceDenial.NotFound"/> answers exactly like a missing resource (<c>404</c>,
    /// <c>not_found</c>), so a caller cannot tell a hidden record from one that does not exist.
    /// </summary>
    /// <remarks>
    /// With several <c>[RequireResource]</c> on one handler, they are checked in declaration order and the first denial
    /// answers, with its own <see cref="DenyAs"/>. Declare a hidden resource first when a later one would reveal it.
    /// </remarks>
    public ResourceDenial DenyAs { get; init; } = ResourceDenial.Forbidden;
}

/// <summary>How a <see cref="RequireResourceAttribute"/> answers a caller it denies.</summary>
public enum ResourceDenial {
    /// <summary>Forbidden (<c>403</c>): the caller learns the resource exists.</summary>
    Forbidden = 0,

    /// <summary>
    /// Not found (<c>404</c>, the generic not-found message): indistinguishable from a resource that does not exist — for
    /// records whose existence is itself confidential, matching a list filter that hides them.
    /// </summary>
    NotFound = 1
}
