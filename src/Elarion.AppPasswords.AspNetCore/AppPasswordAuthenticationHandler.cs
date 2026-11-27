using System.Buffers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>
/// The app-password scheme (ADR-0111): a feed token from the path (<see cref="AppPasswordAuthenticationOptions.FeedTokenReader"/>)
/// or an HTTP Basic credential (split at the first colon), then the events in order. Hand-written over
/// <see cref="AuthenticationHandler{TOptions}"/> with no reflection, so it is NativeAOT-safe.
/// </summary>
internal sealed class AppPasswordAuthenticationHandler(
    IOptionsMonitor<AppPasswordAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AppPasswordAuthenticationOptions>(options, logger, encoder) {
    private const string BasicPrefix = "Basic ";

    // A Basic header longer than this is refused unread: the secret is capped at 512 characters, and a user name is
    // an e-mail address or an id.
    private const int MaxAuthorizationLength = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private new AppPasswordAuthenticationEvents Events {
        get => (AppPasswordAuthenticationEvents)base.Events!;
        set => base.Events = value;
    }

    protected override Task<object> CreateEventsAsync() {
        return Task.FromResult<object>(new AppPasswordAuthenticationEvents());
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync() {
        AppPasswordCredentialKind kind;
        string? userName = null;
        string secret;
        if (Options.FeedTokenReader?.Invoke(Request) is { } token) {
            kind = AppPasswordCredentialKind.Feed;
            secret = token;
        }
        else {
            string? authorization = Request.Headers.Authorization;
            if (authorization is null || !authorization.StartsWith(BasicPrefix, StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.NoResult();
            if (!TryDecodeBasic(authorization.AsSpan(BasicPrefix.Length).Trim(), out var decodedUser, out var decodedSecret))
                return AuthenticateResult.Fail("The Basic credential is malformed.");
            kind = AppPasswordCredentialKind.Basic;
            userName = decodedUser;
            secret = decodedSecret;
        }

        if (secret.Length == 0) return AuthenticateResult.Fail("The credential is empty.");

        var credential = new AppPasswordCredentialContext(Context, Scheme, Options, kind, userName, secret);
        var match = await Events.VerifyCredential(credential).ConfigureAwait(false);
        // Checked here whatever the verifier: a feed token never works as a Basic password, nor the reverse.
        if (match is null || match.Scope.Kind != credential.RequiredKind)
            return AuthenticateResult.Fail("The app password is unknown, revoked or of another scope.");

        if (kind == AppPasswordCredentialKind.Basic) {
            var userNameContext = new AppPasswordUserNameContext(Context, Scheme, Options, userName!, match);
            if (!await Events.ValidateUserName(userNameContext).ConfigureAwait(false))
                return AuthenticateResult.Fail("The user name does not belong to the app password.");
        }

        var principalContext = new AppPasswordPrincipalContext(Context, Scheme, Options, kind, userName, match);
        var principal = await Events.CreatePrincipal(principalContext).ConfigureAwait(false);
        if (principal is null) return AuthenticateResult.Fail("The app password's user may not sign in.");

        principal = AddAppPasswordClaims(principal, match);
        await Events.Authenticated(
                new AppPasswordAuthenticatedContext(Context, Scheme, Options, kind, userName, match, principal))
            .ConfigureAwait(false);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) {
        // A feed URL is the credential: a refused or missing token is a feed that does not exist (spec 0006, FR-026),
        // and a Basic challenge would make a browser ask for a password the feed never takes.
        if (Options.FeedTokenReader?.Invoke(Request) is not null) {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.Append(HeaderNames.WWWAuthenticate, $"Basic realm=\"{Options.Realm}\", charset=\"UTF-8\"");
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) {
        // No challenge: the credential is fine, the user may not do this, and a client re-prompting would not help.
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    private static bool TryDecodeBasic(ReadOnlySpan<char> encoded, out string userName, out string secret) {
        userName = "";
        secret = "";
        if (encoded.IsEmpty || encoded.Length > MaxAuthorizationLength) return false;
        var buffer = ArrayPool<byte>.Shared.Rent(encoded.Length);
        try {
            if (!Convert.TryFromBase64Chars(encoded, buffer, out var written)) return false;
            string decoded;
            try {
                decoded = StrictUtf8.GetString(buffer, 0, written);
            }
            catch (DecoderFallbackException) {
                return false;
            }

            // RFC 7617: the user-id cannot contain a colon, the password can, so split at the first one.
            var colon = decoded.IndexOf(':');
            if (colon < 0) return false;
            userName = decoded[..colon];
            secret = decoded[(colon + 1)..];
            return true;
        }
        finally {
            // The buffer held the secret; clear it before it returns to the shared pool.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private ClaimsPrincipal AddAppPasswordClaims(ClaimsPrincipal principal, AppPasswordMatch match) {
        // The principal comes from the application; an identity it left unauthenticated would sign nobody in, so the
        // scheme authenticates a copy of it under its own name.
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity) {
            var source = principal.Identity as ClaimsIdentity;
            identity = new ClaimsIdentity(
                source?.Claims ?? [], Scheme.Name,
                source?.NameClaimType ?? ClaimsIdentity.DefaultNameClaimType,
                source?.RoleClaimType ?? ClaimsIdentity.DefaultRoleClaimType);
            principal = new ClaimsPrincipal(identity);
        }

        identity.AddClaim(new Claim(AppPasswordClaimTypes.Id, match.Id, ClaimValueTypes.String, ClaimsIssuer));
        identity.AddClaim(new Claim(AppPasswordClaimTypes.Scope, match.Scope.Value, ClaimValueTypes.String, ClaimsIssuer));
        return principal;
    }
}
