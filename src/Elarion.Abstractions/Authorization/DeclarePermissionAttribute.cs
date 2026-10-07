namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Declares the permission <c>{resource}.{verb}</c> in the generated permission catalog (<c>IPermissionCatalog</c>
/// and <c>ElarionPermissions</c>) <b>without</b> requiring it anywhere. Use it for a permission that is not the whole
/// gate of a handler — one a row-level rule or domain service checks on top of a coarser
/// <see cref="RequirePermissionAttribute"/> — so role policy derived from the catalog can still grant it.
/// </summary>
/// <remarks>
/// <para>
/// Place it on the type that reads the permission (a <c>class</c> or <c>record</c>) inside a module's namespace; the
/// permission joins that module's catalog exactly like a <see cref="RequirePermissionAttribute"/> would. It has no
/// runtime effect: nothing is enforced, so the rule that reads it stays responsible for the check (for example
/// through <c>ICurrentUser</c>'s permission claims).
/// </para>
/// <para>
/// Adding a second <see cref="RequirePermissionAttribute"/> to the handler instead is wrong for such a permission:
/// several requirements combine with AND, so "author <i>or</i> manager may delete" would lock the author out.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [DeclarePermission("comments", Verbs.Manage)]   // "comments.manage" is granted by role policy and read below
/// public sealed record CommentAccess(ICurrentUser User) {
///     public bool CanDelete(Comment comment) =&gt;
///         comment.AuthorId == User.UserId || User.HasClaim("permission", ElarionPermissions.Comments.Manage);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = true)]
public sealed class DeclarePermissionAttribute(string resource, string verb) : Attribute {
    /// <summary>The resource the permission applies to (e.g. <c>"comments"</c>).</summary>
    public string Resource { get; } = resource;

    /// <summary>The verb/action the permission grants (e.g. <c>"manage"</c>); see <see cref="Verbs"/>.</summary>
    public string Verb { get; } = verb;

    /// <summary>The composed permission string (<c>{Resource}.{Verb}</c>), as <see cref="RequirePermissionAttribute"/> composes it.</summary>
    public string Permission { get; } = resource + RequirePermissionAttribute.Separator + verb;
}
