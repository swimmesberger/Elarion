using Elarion.Abstractions;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Grpc;

/// <summary>Registration helpers for the host-neutral unary gRPC transport adapter.</summary>
public static class GrpcServiceCollectionExtensions {
    /// <summary>
    /// Registers the default <see cref="IAppErrorTranslator{TError}"/> for <see cref="RpcException"/> with
    /// <c>TryAdd</c> semantics. This does not add gRPC hosting services; the host still chooses and configures its
    /// gRPC server implementation.
    /// </summary>
    public static IServiceCollection AddElarionGrpcTransport(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAppErrorTranslator<RpcException>, GrpcAppErrorTranslator>();
        return services;
    }
}
