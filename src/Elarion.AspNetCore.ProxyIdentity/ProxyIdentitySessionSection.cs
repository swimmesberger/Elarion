using System.Text.Json.Serialization;
using Elarion.Abstractions.Identity;
using Elarion.Session;
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
    /// The cookie a development user switcher sets (<see cref="ProxyDevelopmentIdentityOptions.UserCookie"/>); only
    /// present for the Development stand-in identity.
    /// </summary>
    public string? DevelopmentUserCookie { get; init; }
}

/// <summary>Source-generated JSON metadata for <see cref="ProxyIdentitySection"/>.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProxyIdentitySection))]
public sealed partial class ProxyIdentitySessionJsonContext : JsonSerializerContext;

/// <summary>Contributes <see cref="ProxyIdentitySection"/> under <see cref="SectionName"/>; omitted for an anonymous caller.</summary>
internal sealed class ProxyIdentitySessionContributor(ICurrentUser user, IOptions<ProxyIdentityOptions> options)
    : IClientSnapshotContributor {
    public const string SectionName = "proxyIdentity";

    string IClientSnapshotContributor.SectionName => SectionName;

    public ValueTask<object?> GetSectionAsync(CancellationToken ct) {
        var proxy = options.Value;
        if (ExternalIdentity.TryRead(user, proxy) is not { } identity) {
            return ValueTask.FromResult<object?>(null);
        }
        return ValueTask.FromResult<object?>(new ProxyIdentitySection {
            Email = identity.Email,
            Name = identity.Name,
            LogoutUrl = string.IsNullOrWhiteSpace(proxy.LogoutUrl) ? null : proxy.LogoutUrl,
            IsDevelopmentIdentity = identity.IsDevelopmentIdentity,
            DevelopmentUserCookie = identity.IsDevelopmentIdentity && !string.IsNullOrEmpty(proxy.Development.UserCookie)
                ? proxy.Development.UserCookie
                : null,
        });
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
        return services.AddElarionClientSnapshotContributor<ProxyIdentitySessionContributor>(ProxyIdentitySessionJsonContext.Default);
    }
}
