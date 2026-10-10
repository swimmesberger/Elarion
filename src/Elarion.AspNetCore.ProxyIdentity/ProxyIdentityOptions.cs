namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// Configuration of the proxy identity integration (section <see cref="ProxyIdentityDefaults.SectionName"/>). The
/// authenticating reverse proxy signs the person in and forwards a short-lived signed token with every request; these
/// options say whom to trust (<see cref="Issuer"/>, the signing keys, <see cref="Audiences"/>), where the token travels
/// (<see cref="TokenHeader"/>, <see cref="TokenCookie"/>) and how its claims are named (<see cref="Claims"/>).
/// </summary>
/// <remarks>
/// <para>
/// Signing keys come from exactly one source: <see cref="JwksUrl"/> (a bare JSON Web Key Set, e.g. Cloudflare Access or
/// Google IAP), <see cref="MetadataAddress"/> (an OpenID Connect discovery document), or — when neither is set — the
/// discovery document at <c>{Issuer}/.well-known/openid-configuration</c>. Either way IdentityModel's
/// <c>ConfigurationManager</c> fetches, caches and refreshes them.
/// </para>
/// <para>
/// <c>AddElarionProxyIdentity</c> validates these options when it is called and throws when they are incomplete, and
/// it refuses <see cref="Enabled"/> = <see langword="false"/> outside the Development environment.
/// </para>
/// </remarks>
public sealed class ProxyIdentityOptions {
    /// <summary>
    /// Whether proxy tokens are validated. <see langword="false"/> selects the Development stand-in identity
    /// (<see cref="Development"/>), which is refused outside the Development environment: a deployment that forgot its
    /// proxy configuration must not start as "everybody is the development user". Defaults to <see langword="false"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>The exact <c>iss</c> value the proxy stamps into its tokens. Required when <see cref="Enabled"/>.</summary>
    public string? Issuer { get; set; }

    /// <summary>
    /// The URL of the JSON Web Key Set that signs the tokens, for proxies that publish keys without a discovery document
    /// (Cloudflare Access: <c>https://&lt;team&gt;.cloudflareaccess.com/cdn-cgi/access/certs</c>; Google IAP:
    /// <c>https://www.gstatic.com/iap/verify/public_key-jwk</c>). Mutually exclusive with <see cref="MetadataAddress"/>.
    /// </summary>
    public string? JwksUrl { get; set; }

    /// <summary>
    /// The URL of an OpenID Connect discovery document whose <c>jwks_uri</c> names the signing keys. Leave it and
    /// <see cref="JwksUrl"/> empty to use <c>{Issuer}/.well-known/openid-configuration</c>. The document's <c>issuer</c>
    /// must equal <see cref="Issuer"/> exactly, or it is refused (and a refreshed one is ignored in favour of the last
    /// accepted one).
    /// </summary>
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// Whether <see cref="JwksUrl"/>, <see cref="MetadataAddress"/> and the URLs a discovery document names must use
    /// https. Defaults to <see langword="true"/>; turn it off only for a local test issuer.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// The accepted <c>aud</c> values. Each entry may itself be a comma-separated list, so one environment variable can
    /// carry several. Required when <see cref="Enabled"/>, unless <see cref="AllowAnyAudience"/> is set.
    /// </summary>
    public IList<string> Audiences { get; set; } = [];

    /// <summary>
    /// Accept a token for any audience of <see cref="Issuer"/>. Safe only while this is the issuer's sole application:
    /// otherwise a token minted for a sibling application is accepted here. Never the default; it is logged as a warning
    /// at startup and cannot be combined with <see cref="Audiences"/>.
    /// </summary>
    public bool AllowAnyAudience { get; set; }

    /// <summary>
    /// The request header carrying the token (Cloudflare Access: <c>Cf-Access-Jwt-Assertion</c>; Google IAP:
    /// <c>x-goog-iap-jwt-assertion</c>; oauth2-proxy: <c>Authorization</c>). A <c>Bearer </c> prefix is stripped. At
    /// least one of <see cref="TokenHeader"/> and <see cref="TokenCookie"/> is required when <see cref="Enabled"/>; no
    /// other location is read.
    /// </summary>
    public string? TokenHeader { get; set; }

    /// <summary>
    /// A cookie read when <see cref="TokenHeader"/> is absent or empty (Cloudflare Access: <c>CF_Authorization</c>).
    /// Useful for requests the browser makes without the proxy injecting a header, such as an <c>EventSource</c>.
    /// </summary>
    public string? TokenCookie { get; set; }

    /// <summary>The claim names of the proxy's token.</summary>
    public ProxyIdentityClaimNames Claims { get; set; } = new();

    /// <summary>
    /// How far the token's e-mail address may be trusted, which decides <see cref="ExternalIdentity.EmailVerified"/>.
    /// Defaults to <see cref="ProxyEmailTrust.Claim"/>.
    /// </summary>
    public ProxyEmailTrust EmailTrust { get; set; } = ProxyEmailTrust.Claim;

    /// <summary>Where the browser signs out of the proxy (for example <c>/cdn-cgi/access/logout</c>), or null for none.</summary>
    public string? LogoutUrl { get; set; }

    /// <summary>
    /// The maximum token clock skew tolerated for <c>exp</c>/<c>nbf</c>. Defaults to one minute: proxy tokens are
    /// short-lived, so JwtBearer's five-minute default would extend them noticeably.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long fetched signing keys are used before they are fetched again in the background (IdentityModel's
    /// <c>AutomaticRefreshInterval</c>). Defaults to one hour; the library minimum is five minutes.
    /// </summary>
    public TimeSpan AutomaticRefreshInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The minimum time between two fetches triggered by a token naming an unknown signing key (IdentityModel's
    /// <c>RefreshInterval</c>), which bounds how often forged or stale tokens can make this instance call the key
    /// endpoint. Defaults to one minute; the library minimum is one second.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a signing-key configuration that validated a token stays usable after a refresh replaced it
    /// (IdentityModel's last-known-good cache). Defaults to <see cref="TimeSpan.Zero"/>, which turns the cache off: a key
    /// the issuer withdrew stops validating as soon as the refresh lands. IdentityModel's own default is one hour, which
    /// keeps accepting tokens signed with a withdrawn key for that long; set a positive value only for an issuer that
    /// withdraws keys while tokens signed with them are still in use. An outage never needs it — a failed or refused
    /// refresh keeps the current keys either way.
    /// </summary>
    public TimeSpan LastKnownGoodLifetime { get; set; } = TimeSpan.Zero;

    /// <summary>The Development stand-in identity used while <see cref="Enabled"/> is <see langword="false"/>.</summary>
    public ProxyDevelopmentIdentityOptions Development { get; set; } = new();

    /// <summary>The configured audiences with comma-separated entries split, trimmed and de-duplicated.</summary>
    internal IReadOnlyList<string> EffectiveAudiences() {
        return Audiences
            .SelectMany(entry => (entry ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}

/// <summary>
/// Claim names of the proxy's token. Optional names may be set to null (or empty) when the proxy sends no such claim.
/// Inbound claim names are kept verbatim (JwtBearer's <c>MapInboundClaims</c> is off), so these are the names as they
/// appear in the token.
/// </summary>
public sealed class ProxyIdentityClaimNames {
    /// <summary>The stable, never reassigned subject. Defaults to <c>sub</c>; required.</summary>
    public string Subject { get; set; } = "sub";

    /// <summary>The e-mail address. Defaults to <c>email</c>.</summary>
    public string? Email { get; set; } = "email";

    /// <summary>The boolean claim read under <see cref="ProxyEmailTrust.Claim"/>. Defaults to <c>email_verified</c>.</summary>
    public string? EmailVerified { get; set; } = "email_verified";

    /// <summary>The display name. Defaults to <c>name</c>.</summary>
    public string? Name { get; set; } = "name";

    /// <summary>
    /// The multi-valued claim naming the person's groups at the identity provider. Defaults to <c>groups</c>. Groups are
    /// facts about the person, not application roles: they are exposed on <see cref="ExternalIdentity.Groups"/> for the
    /// application to map, and are not added to <c>ICurrentUser.Roles</c>.
    /// </summary>
    public string? Groups { get; set; } = "groups";

    /// <summary>
    /// The multi-valued claim naming the person's roles for this application: the <c>roles</c> claim of RFC 9068
    /// §2.2.3.1 (a JSON array of strings; a single string is read too). Defaults to <c>roles</c>. It is the role claim
    /// type of the principal, so it populates <c>ICurrentUser.Roles</c> and drives <c>[RequireRole]</c>.
    /// </summary>
    public string? Roles { get; set; } = "roles";
}

/// <summary>Whether the token's e-mail address may be trusted as verified (<see cref="ExternalIdentity.EmailVerified"/>).</summary>
public enum ProxyEmailTrust {
    /// <summary>The address counts as verified exactly when the token's <see cref="ProxyIdentityClaimNames.EmailVerified"/> claim is <c>true</c>.</summary>
    Claim,

    /// <summary>The proxy vouches for every address it forwards (a one-time-PIN login, a trusted corporate directory).</summary>
    Trusted,

    /// <summary>Never trust the address (for example an identity provider whose <c>email</c> is user-editable).</summary>
    None,
}

/// <summary>
/// The Development stand-in identity: every request is signed in with the same claim shape a proxy token carries. The
/// request header <see cref="UserHeader"/> or the cookie <see cref="UserCookie"/> switches to another user — one of
/// <see cref="Users"/> when its e-mail address or subject matches, otherwise an ad-hoc user with that e-mail address and
/// no groups or roles.
/// </summary>
public sealed class ProxyDevelopmentIdentityOptions {
    /// <summary>The subject of the default stand-in user. Defaults to <c>developer</c>.</summary>
    public string Subject { get; set; } = "developer";

    /// <summary>The e-mail address of the default stand-in user. Defaults to <c>developer@example.com</c>.</summary>
    public string Email { get; set; } = "developer@example.com";

    /// <summary>The display name of the default stand-in user. Defaults to <c>Developer</c>.</summary>
    public string? Name { get; set; } = "Developer";

    /// <summary>The groups of the default stand-in user.</summary>
    public IList<string> Groups { get; set; } = [];

    /// <summary>The roles of the default stand-in user.</summary>
    public IList<string> Roles { get; set; } = [];

    /// <summary>Further stand-in users the switch can select by e-mail address (case-insensitive) or subject.</summary>
    public IList<ProxyDevelopmentUser> Users { get; set; } = [];

    /// <summary>The request header that switches the user, or null/empty to disable it. Defaults to <see cref="ProxyIdentityDefaults.DevelopmentUserHeader"/>.</summary>
    public string? UserHeader { get; set; } = ProxyIdentityDefaults.DevelopmentUserHeader;

    /// <summary>The cookie that switches the user (URL-encoded), or null/empty to disable it. Defaults to <see cref="ProxyIdentityDefaults.DevelopmentUserCookie"/>.</summary>
    public string? UserCookie { get; set; } = ProxyIdentityDefaults.DevelopmentUserCookie;
}

/// <summary>A named Development stand-in user (<see cref="ProxyDevelopmentIdentityOptions.Users"/>).</summary>
public sealed class ProxyDevelopmentUser {
    /// <summary>The subject. Defaults to <c>dev:{email}</c> (lower-cased) when empty.</summary>
    public string? Subject { get; set; }

    /// <summary>The e-mail address; required.</summary>
    public string Email { get; set; } = "";

    /// <summary>The display name, or null.</summary>
    public string? Name { get; set; }

    /// <summary>The groups.</summary>
    public IList<string> Groups { get; set; } = [];

    /// <summary>The roles.</summary>
    public IList<string> Roles { get; set; } = [];
}
