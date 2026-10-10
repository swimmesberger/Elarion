namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>Names used by the proxy identity integration: configuration section, schemes, and the development switch.</summary>
public static class ProxyIdentityDefaults {
    /// <summary>The configuration section <see cref="ProxyIdentityOptions"/> binds from.</summary>
    public const string SectionName = "ProxyIdentity";

    /// <summary>The JwtBearer scheme that validates the token the proxy forwards.</summary>
    public const string AuthenticationScheme = "ProxyIdentity";

    /// <summary>The Development-only scheme that signs every request in as the stand-in identity.</summary>
    public const string DevelopmentScheme = "ProxyIdentityDevelopment";

    /// <summary>The <c>iss</c> claim of every Development stand-in identity, so it can never be mistaken for a real one.</summary>
    public const string DevelopmentIssuer = "urn:elarion:proxy-identity:development";

    /// <summary>
    /// The named <see cref="System.Net.Http.HttpClient"/> the signing keys and discovery document are fetched with.
    /// Configure it with <c>services.AddHttpClient(ProxyIdentityDefaults.HttpClientName)</c> to add a proxy, a
    /// handler, or a different timeout (the default is 10 seconds).
    /// </summary>
    public const string HttpClientName = "Elarion.ProxyIdentity";

    /// <summary>The request header that switches the Development stand-in to another user (an e-mail address or subject).</summary>
    public const string DevelopmentUserHeader = "X-Elarion-Dev-User";

    /// <summary>The cookie that switches the Development stand-in to another user (an URL-encoded e-mail address or subject).</summary>
    public const string DevelopmentUserCookie = "elarion-dev-user";
}
