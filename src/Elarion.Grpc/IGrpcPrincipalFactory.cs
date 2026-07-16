using System.Security.Claims;
using Grpc.Core;

namespace Elarion.Grpc;

/// <summary>
/// Returns the principal already authenticated by the gRPC host for a call. Authentication itself remains a host
/// concern; this seam only captures its result so every unary invocation seeds Elarion's dispatch scope uniformly.
/// </summary>
public interface IGrpcPrincipalFactory {
    /// <summary>Returns the authenticated principal for <paramref name="context"/>.</summary>
    ClaimsPrincipal CreatePrincipal(ServerCallContext context);
}
