using System.Security.Claims;
using Elarion.Abstractions;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Grpc;

/// <summary>Registration helpers for the host-neutral gRPC transport adapter.</summary>
public static class GrpcServiceCollectionExtensions {
    /// <summary>
    /// Registers the unary and server-streaming invokers, default error translator, and a delegate that returns
    /// the principal already authenticated by the host. This does not add or configure a gRPC server.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="createPrincipal">Returns the host-authenticated principal for each call.</param>
    public static IServiceCollection AddElarionGrpcTransport(
        this IServiceCollection services,
        Func<ServerCallContext, ClaimsPrincipal> createPrincipal) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(createPrincipal);

        services.TryAddSingleton<IGrpcPrincipalFactory>(new DelegateGrpcPrincipalFactory(createPrincipal));
        AddTransportServices(services);
        return services;
    }

    /// <summary>
    /// Registers the unary and server-streaming invokers, default error translator, and
    /// <paramref name="principalFactory"/> as the host-specific principal source. This overload avoids
    /// reflection-based activation and does not add or configure a gRPC server.
    /// </summary>
    public static IServiceCollection AddElarionGrpcTransport(
        this IServiceCollection services,
        IGrpcPrincipalFactory principalFactory) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(principalFactory);

        services.TryAddSingleton(principalFactory);
        AddTransportServices(services);
        return services;
    }

    private static void AddTransportServices(IServiceCollection services) {
        services.TryAddSingleton<IAppErrorTranslator<RpcException>, GrpcAppErrorTranslator>();
        services.TryAddTransient<GrpcHandlerInvoker>();
        services.TryAddTransient<GrpcStreamHandlerInvoker>();
    }

    private sealed class DelegateGrpcPrincipalFactory(
        Func<ServerCallContext, ClaimsPrincipal> createPrincipal) : IGrpcPrincipalFactory {
        public ClaimsPrincipal CreatePrincipal(ServerCallContext context) {
            return createPrincipal(context);
        }
    }
}
