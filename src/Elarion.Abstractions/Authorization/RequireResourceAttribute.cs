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
    public string? ResourceTypeName { get; set; }

    /// <summary>The operation name (an open <see cref="ResourceOperation"/> value). Defaults to <c>"read"</c>.</summary>
    public string Operation { get; set; } = "read";

    /// <summary>
    /// The request property path identifying the resource id, as a compile-checked path — typically
    /// <c>nameof(Req.Id)</c>, or a dotted path (<c>nameof(Req.Customer) + "." + nameof(Customer.Id)</c>).
    /// Defaults to <c>"Id"</c>. A path that names no such member is a generator diagnostic (ELAUTH002).
    /// </summary>
    public string Id { get; set; } = "Id";
}
