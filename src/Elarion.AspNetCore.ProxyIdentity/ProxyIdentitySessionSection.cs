using System.Text.Json.Serialization;
using Elarion.Abstractions.Identity;
using Elarion.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Elarion.AspNetCore.ProxyIdentity;

/// <summary>
/// The <c>proxyIdentity</c> section of the client-capability snapshot: who the proxy signed in and where to sign out,
/// so a frontend can render its account menu without a second round trip. A read-only UX projection like every
/// snapshot section; it grants nothing.
/// </summary>
public sealed record ProxyIdentitySection {
    /// <summary>The signed-in e-mail address, or null.</summary>
    public string? Email { get; init; }

    /// <summary>The signed-in display name, or null.</summary>
    public string? Name { get; init; }

    /// <summary>Where the browser signs out of the proxy (<see cref="ProxyIdentityOptions.LogoutUrl"/>), or null.</summary>
    public string? LogoutUrl { get; init; }

    /// <summary>Whether this is the Development stand-in identity.</summary>
    public bool IsDevelopmentIdentity { get; init; }

    /// <summary>
    /// The cookie a development user switcher sets (<see cref="ProxyDevelopmentIdentityOptions.UserCookie"/>); present
    /// whenever the Development stand-in is configured, whoever is signed in.
    /// </summary>
    public string? DevelopmentUserCookie { get; init; }

    /// <summary>
    /// The cookie a development switcher sets to choose the stand-in's roles
    /// (<see cref="ProxyDevelopmentIdentityOptions.RolesCookie"/>); present whenever the Development stand-in is configured.
    /// </summary>
    public string? DevelopmentRolesCookie { get; init; }

    /// <summary>
    /// The cookie a development switcher sets to choose the stand-in's groups
    /// (<see cref="ProxyDevelopmentIdentityOptions.GroupsCookie"/>); present whenever the Development stand-in is configured.
    /// </summary>
    public string? DevelopmentGroupsCookie { get; init; }
}

/// <summary>Source-generated JSON metadata for <see cref="ProxyIdentitySection"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProxyIdentitySection))]
public sealed partial class ProxyIdentitySessionJsonContext : JsonSerializerContext;

/// <summary>
/// Contributes <see cref="ProxyIdentitySection"/> under <see cref="SectionName"/>: who the proxy signed in, plus the
/// development switch cookies while the stand-in is configured; omitted when there is neither.
/// </summary>
/// <remarks>
/// The identity comes from the request (<see cref="ExternalIdentity.TryRead(HttpContext, ProxyIdentityOptions)"/>), so
/// it survives an application replacing the principal after authentication; dispatch without an HTTP request falls back
/// to <see cref="ICurrentUser"/>. The switch cookie names are static configuration and do not depend on the identity: a
/// person the application treats as anonymous still gets them, so a development switcher can sign in as someone else.
/// </remarks>
internal sealed class ProxyIdentitySessionContributor(
    ICurrentUser user,
    IOptions<ProxyIdentityOptions> options,
    IHttpContextAccessor httpContextAccessor)
    : IClientSnapshotContributor {
    public const string SectionName = "proxyIdentity";

    string IClientSnapshotContributor.SectionName => SectionName;

    public ValueTask<object?> GetSectionAsync(CancellationToken ct) {
        var proxy = options.Value;
        var identity = (httpContextAccessor.HttpContext is { } context ? ExternalIdentity.TryRead(context, proxy) : null)
            ?? ExternalIdentity.TryRead(user, proxy);
        // The switches exist exactly where the stand-in does: with Enabled false, which is refused outside Development.
        var development = proxy.Enabled ? null : proxy.Development;
        if (identity is null && development is null) {
            return ValueTask.FromResult<object?>(null);
        }
        return ValueTask.FromResult<object?>(new ProxyIdentitySection {
            Email = identity?.Email,
            Name = identity?.Name,
            LogoutUrl = identity is null || string.IsNullOrWhiteSpace(proxy.LogoutUrl) ? null : proxy.LogoutUrl,
            IsDevelopmentIdentity = identity?.IsDevelopmentIdentity ?? false,
            DevelopmentUserCookie = NullIfEmpty(development?.UserCookie),
            DevelopmentRolesCookie = NullIfEmpty(development?.RolesCookie),
            DevelopmentGroupsCookie = NullIfEmpty(development?.GroupsCookie),
        });
    }

    private static string? NullIfEmpty(string? value) {
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

/// <summary>Registration of the optional <c>proxyIdentity</c> session section.</summary>
public static class ProxyIdentitySessionServiceCollectionExtensions {
    /// <summary>
    /// Adds the <c>proxyIdentity</c> section (<see cref="ProxyIdentitySection"/>) to the client-capability snapshot.
    /// Requires <c>AddElarionProxyIdentity</c> and <c>AddElarionSession</c>.
    /// </summary>
    public static IServiceCollection AddElarionProxyIdentitySessionSection(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        // The section reads the identity captured on the request, also from a JSON-RPC or MCP call's dispatch scope.
        services.AddHttpContextAccessor();
        return services.AddElarionClientSnapshotContributor<ProxyIdentitySessionContributor>(ProxyIdentitySessionJsonContext.Default);
    }
}
