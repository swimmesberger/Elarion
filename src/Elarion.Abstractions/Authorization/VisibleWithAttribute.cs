namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Shows a response member only to callers who hold <see cref="Permission"/>: for everyone else the handler pipeline
/// answers the member as <see langword="null"/> (omitted on the wire). The handler fills the member for everybody;
/// the decision is declared once, here, instead of nulling it by hand in each handler.
/// </summary>
/// <remarks>
/// <para>
/// Use it where a caller may run an operation but one part of its answer belongs to a narrower audience: a preview a
/// <c>banking.write</c> holder may run, whose matched stored bookings only <c>banking.read</c> holders may see; an
/// opening balance in a list most users may open. The member must be nullable (a reference type or
/// <see cref="Nullable{T}"/>); its declaring type must be a record (or a struct), because the pipeline answers a copy
/// made with a <c>with</c> expression. Members are found through the handler's response type at compile time —
/// nested records, arrays, lists and other collections of them included — so the check is reflection-free.
/// </para>
/// <para>
/// The decision is the one <see cref="IPermissionChecker"/> makes for the <c>[RequirePermission]</c> gate. Without a
/// registered checker every marked member is hidden (fail closed). The schema exports the requirement as
/// <c>x-elarion-visible-with</c> on the member, so a generated client knows why the member may be missing. Like every
/// client-side projection, that is a UX hint; the pipeline is the authority.
/// </para>
/// <para>
/// On a positional record, target the property: <c>[property: VisibleWith(…)]</c>; a parameter target is accepted too.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed record ImportPreviewLine {
///     public required int Row { get; init; }
///     public required decimal Amount { get; init; }
///
///     [VisibleWith(ElarionPermissions.Banking.Read)]   // or [VisibleWith("banking", Verbs.Read)]
///     public StoredBooking? Resembles { get; init; }
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class VisibleWithAttribute : Attribute {
    /// <summary>Shows the member to holders of <paramref name="permission"/> (<c>{resource}.{verb}</c>).</summary>
    /// <param name="permission">The composed permission, for example <c>ElarionPermissions.Banking.Read</c>.</param>
    public VisibleWithAttribute(string permission) {
        Permission = permission;
    }

    /// <summary>
    /// Shows the member to holders of the permission to perform <paramref name="verb"/> on <paramref name="resource"/>,
    /// composed as <see cref="RequirePermissionAttribute"/> composes it.
    /// </summary>
    public VisibleWithAttribute(string resource, string verb) {
        Permission = resource + RequirePermissionAttribute.Separator + verb;
    }

    /// <summary>The permission a caller must hold to see the member.</summary>
    public string Permission { get; }
}
