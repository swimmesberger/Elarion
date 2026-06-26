namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Opts an assembly or module into secure-by-default authorization: every handler in scope requires
/// authorization unless it carries <see cref="AllowAnonymousAttribute"/>. Resolved most-specific-wins
/// (the handler's module beats the assembly), mirroring how <c>[DefaultPipeline]</c> is scoped.
/// </summary>
/// <remarks>
/// With <see cref="RequireAuthenticated"/> (the default), a handler with no explicit <c>Require*</c>
/// attribute requires an authenticated principal. The source generator reads this at compile time and
/// attaches the authorization decorator to every in-scope, non-anonymous handler.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ElarionAuthorizationDefaultsAttribute : Attribute {
    /// <summary>Whether in-scope handlers require an authenticated principal by default. Defaults to <c>true</c>.</summary>
    public bool RequireAuthenticated { get; set; } = true;
}
