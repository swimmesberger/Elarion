using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore;

/// <summary>
/// Endpoint metadata that exempts an endpoint from <see cref="CrossOriginProtectionApplicationBuilderExtensions.UseElarionCrossOriginProtection"/>:
/// for routes that authenticate every request with a credential of their own that a browser does not attach by itself,
/// such as the DAV routes, which take only app passwords (spec 0006 FR-036).
/// </summary>
/// <remarks>
/// The protection usually runs before routing, when no endpoint is selected yet; then it exempts requests below
/// <see cref="PathPrefix"/>, collected from every endpoint carrying this metadata. When routing ran first, the selected
/// endpoint's metadata decides.
/// </remarks>
public sealed class CrossOriginProtectionExemptMetadata {
    /// <summary>Creates the metadata.</summary>
    /// <param name="pathPrefix">The path prefix of the exempt routes, for requests checked before routing; empty for none.</param>
    public CrossOriginProtectionExemptMetadata(PathString pathPrefix = default) {
        if (pathPrefix.HasValue && pathPrefix.Value == "/")
            throw new ArgumentException("An exempt path prefix must name a path below the root.", nameof(pathPrefix));
        PathPrefix = pathPrefix;
    }

    /// <summary>The path prefix of the exempt routes, matched by whole segments, or empty.</summary>
    public PathString PathPrefix { get; }
}

/// <summary>Exempts endpoints from the cross-origin request protection.</summary>
public static class CrossOriginProtectionEndpointConventionBuilderExtensions {
    /// <summary>
    /// Exempts the endpoints of <paramref name="builder"/> from the cross-origin request protection. Use it only for routes
    /// that authenticate every request with their own non-ambient credential.
    /// </summary>
    /// <param name="builder">The endpoint convention builder.</param>
    /// <param name="pathPrefix">The routes' path prefix, so the protection also exempts them when it runs before routing.</param>
    public static TBuilder DisableElarionCrossOriginProtection<TBuilder>(this TBuilder builder, PathString pathPrefix = default)
        where TBuilder : IEndpointConventionBuilder {
        ArgumentNullException.ThrowIfNull(builder);
        var metadata = new CrossOriginProtectionExemptMetadata(pathPrefix);
        builder.Add(endpoint => endpoint.Metadata.Add(metadata));
        return builder;
    }
}
