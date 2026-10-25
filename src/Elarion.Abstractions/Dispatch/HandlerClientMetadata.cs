namespace Elarion.Abstractions.Dispatch;

/// <summary>
/// What a client may know about an operation before calling it: the requirements it can evaluate against its own
/// session snapshot, and the operation's <see cref="ClientTagAttribute"/> hints. The generated handler registration
/// computes it at compile time and attaches it to the <see cref="HandlerRoute"/>; the RPC schema export writes it
/// per method (<c>requires</c>, <c>tags</c>).
/// </summary>
/// <remarks>
/// It is a UX projection of the handler's declarations, never enforcement: the authorization decorator decides every
/// call. A declared requirement the client cannot evaluate — a named policy, a claim, a resource check — shows up only
/// as <see cref="HasServerChecks"/>. What the handler does not declare is invisible here: an
/// <c>IGlobalAuthorizationRule</c>, a feature gate, or a business rule can still refuse a call this metadata allows.
/// </remarks>
public sealed record HandlerClientMetadata {
    private static readonly IReadOnlyDictionary<string, string> NoTags = new Dictionary<string, string>();

    /// <summary>The handler is <c>[AllowAnonymous]</c>: any caller may call it, and no other requirement applies.</summary>
    public bool AllowAnonymous { get; init; }

    /// <summary>
    /// The caller must be authenticated — because the handler declares a requirement or an
    /// <c>[ElarionAuthorizationDefaults]</c> in its scope requires it.
    /// </summary>
    public bool RequiresAuthentication { get; init; }

    /// <summary>The permissions the handler requires, all of them (<c>[RequirePermission]</c>), in ordinal order.</summary>
    public IReadOnlyList<string> Permissions { get; init; } = [];

    /// <summary>The roles the handler requires, all of them (<c>[RequireRole]</c>), in ordinal order.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// The handler declares a requirement only the server can evaluate: <c>[RequirePolicy]</c>,
    /// <c>[RequireClaim]</c> or <c>[RequireResource]</c>. A client treats the operation as possibly refused. Global
    /// authorization rules are not declarations of the handler and never set it.
    /// </summary>
    public bool HasServerChecks { get; init; }

    /// <summary>The operation's <see cref="ClientTagAttribute"/> hints by key.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = NoTags;
}
