using Elarion.Grpc;
using Grpc.AspNetCore.Server;
using Grpc.Core;

namespace Elarion.Grpc.AspNetCore;

/// <summary>Composes Elarion's handler transports with an ASP.NET Core grpc-dotnet server.</summary>
public static class ElarionGrpcServerBuilderExtensions {
    /// <summary>
    /// Adds Elarion's unary and request-driven server-streaming transports to a server registered with
    /// <c>AddGrpc()</c>. The authenticated
    /// <see cref="System.Security.Claims.ClaimsPrincipal"/> is captured from the call's ASP.NET Core
    /// <c>HttpContext.User</c>; no per-method principal plumbing is required.
    /// </summary>
    /// <param name="builder">The grpc-dotnet server builder.</param>
    /// <returns>The same builder for further grpc-dotnet configuration.</returns>
    public static IGrpcServerBuilder AddElarion(this IGrpcServerBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddElarionGrpcTransport(static context => context.GetHttpContext().User);
        return builder;
    }
}
