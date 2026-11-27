using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>
/// What every app-password event sees: the request, its services, the scheme, how the credential was presented, the
/// Basic user name and the request's cancellation token. The events receive a delegate over a context rather than
/// bound parameters, so the scheme needs no reflection under NativeAOT; <see cref="Services"/> gives the same reach.
/// </summary>
public abstract class AppPasswordContext : BaseContext<AppPasswordAuthenticationOptions> {
    /// <summary>Creates the context.</summary>
    protected AppPasswordContext(
        HttpContext context,
        AuthenticationScheme scheme,
        AppPasswordAuthenticationOptions options,
        AppPasswordCredentialKind credentialKind,
        string? userName)
        : base(context, scheme, options) {
        CredentialKind = credentialKind;
        UserName = userName;
    }

    /// <summary>The request scope's services.</summary>
    public IServiceProvider Services => HttpContext.RequestServices;

    /// <summary>How the client presented its credential.</summary>
    public AppPasswordCredentialKind CredentialKind { get; }

    /// <summary>The user name the client sent over HTTP Basic; <see langword="null"/> for a feed token.</summary>
    public string? UserName { get; }

    /// <summary>The request's cancellation token (<see cref="HttpContext.RequestAborted"/>).</summary>
    public CancellationToken CancellationToken => HttpContext.RequestAborted;
}

/// <summary>The context of <see cref="AppPasswordAuthenticationEvents.OnVerifyCredential"/>.</summary>
public sealed class AppPasswordCredentialContext : AppPasswordContext {
    /// <summary>Creates the context.</summary>
    public AppPasswordCredentialContext(
        HttpContext context,
        AuthenticationScheme scheme,
        AppPasswordAuthenticationOptions options,
        AppPasswordCredentialKind credentialKind,
        string? userName,
        string secret)
        : base(context, scheme, options, credentialKind, userName) {
        Secret = secret;
    }

    /// <summary>The secret the client sent: the Basic password or the feed token. Never log it.</summary>
    public string Secret { get; }

    /// <summary>
    /// The scope kind the scheme accepts for this request: <see cref="AppPasswordScopeKind.Dav"/> over HTTP Basic,
    /// <see cref="AppPasswordScopeKind.CalendarFeed"/> for a feed token. A match of another kind is refused.
    /// </summary>
    public AppPasswordScopeKind RequiredKind =>
        CredentialKind == AppPasswordCredentialKind.Feed ? AppPasswordScopeKind.CalendarFeed : AppPasswordScopeKind.Dav;
}

/// <summary>The context of <see cref="AppPasswordAuthenticationEvents.OnValidateUserName"/>.</summary>
public sealed class AppPasswordUserNameContext : AppPasswordContext {
    /// <summary>Creates the context.</summary>
    public AppPasswordUserNameContext(
        HttpContext context,
        AuthenticationScheme scheme,
        AppPasswordAuthenticationOptions options,
        string userName,
        AppPasswordMatch match)
        : base(context, scheme, options, AppPasswordCredentialKind.Basic, userName) {
        Match = match;
    }

    /// <summary>The verified credential.</summary>
    public AppPasswordMatch Match { get; }

    /// <summary>The user name the client sent over HTTP Basic.</summary>
    public new string UserName => base.UserName!;
}

/// <summary>The context of <see cref="AppPasswordAuthenticationEvents.OnCreatePrincipal"/>.</summary>
public sealed class AppPasswordPrincipalContext : AppPasswordContext {
    /// <summary>Creates the context.</summary>
    public AppPasswordPrincipalContext(
        HttpContext context,
        AuthenticationScheme scheme,
        AppPasswordAuthenticationOptions options,
        AppPasswordCredentialKind credentialKind,
        string? userName,
        AppPasswordMatch match)
        : base(context, scheme, options, credentialKind, userName) {
        Match = match;
    }

    /// <summary>The verified credential; <see cref="AppPasswordMatch.UserId"/> is the user to sign in.</summary>
    public AppPasswordMatch Match { get; }
}

/// <summary>The context of <see cref="AppPasswordAuthenticationEvents.OnAuthenticated"/>.</summary>
public sealed class AppPasswordAuthenticatedContext : AppPasswordContext {
    /// <summary>Creates the context.</summary>
    public AppPasswordAuthenticatedContext(
        HttpContext context,
        AuthenticationScheme scheme,
        AppPasswordAuthenticationOptions options,
        AppPasswordCredentialKind credentialKind,
        string? userName,
        AppPasswordMatch match,
        ClaimsPrincipal principal)
        : base(context, scheme, options, credentialKind, userName) {
        Match = match;
        Principal = principal;
    }

    /// <summary>The verified credential.</summary>
    public AppPasswordMatch Match { get; }

    /// <summary>The principal the request is signed in as, with the app-password claims.</summary>
    public ClaimsPrincipal Principal { get; }
}
