using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Grpc.AspNetCore;

/// <summary>ASP.NET Core grpc-dotnet convenience methods for invoking Elarion handlers.</summary>
public static class ElarionServerCallContextExtensions {
    /// <summary>
    /// Maps and invokes a unary Elarion handler using this call's request services and authenticated principal.
    /// </summary>
    /// <typeparam name="TWireRequest">The generated protobuf request type.</typeparam>
    /// <typeparam name="TWireResponse">The generated protobuf response type.</typeparam>
    /// <typeparam name="TRequest">The application handler request type.</typeparam>
    /// <typeparam name="TResponse">The application handler success type.</typeparam>
    /// <param name="context">The grpc-dotnet call context.</param>
    /// <param name="wireRequest">The generated protobuf request.</param>
    /// <param name="mapRequest">Maps the protobuf request into the application request.</param>
    /// <param name="mapResponse">Maps the successful application response into the protobuf response.</param>
    /// <returns>The mapped protobuf response.</returns>
    /// <exception cref="RpcException">The registered translator's representation of a failed result.</exception>
    public static Task<TWireResponse> InvokeElarionAsync<TWireRequest, TWireResponse, TRequest, TResponse>(
        this ServerCallContext context,
        TWireRequest wireRequest,
        Func<TWireRequest, TRequest> mapRequest,
        Func<TResponse, TWireResponse> mapResponse)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(context);

        var invoker = context.GetHttpContext().RequestServices.GetRequiredService<GrpcHandlerInvoker>();
        return invoker.InvokeUnaryAsync(wireRequest, context, mapRequest, mapResponse);
    }
}
