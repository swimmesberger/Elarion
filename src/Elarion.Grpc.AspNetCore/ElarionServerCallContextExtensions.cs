using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Elarion;

namespace Elarion.Grpc.AspNetCore;

/// <summary>ASP.NET Core grpc-dotnet convenience methods for invoking Elarion handlers.</summary>
public static class ElarionServerCallContextExtensions
{
    /// <summary>
    /// Invokes an application request directly using this call's request services and authenticated principal.
    /// </summary>
    /// <typeparam name="TRequest">The application handler request type.</typeparam>
    /// <typeparam name="TResponse">The application handler success type.</typeparam>
    /// <param name="context">The grpc-dotnet call context.</param>
    /// <param name="request">The application request to dispatch.</param>
    /// <returns>The successful application response.</returns>
    /// <exception cref="RpcException">The registered translator's representation of a failed result.</exception>
    public static Task<TResponse> InvokeElarionAsync<TRequest, TResponse>(
        this ServerCallContext context,
        TRequest request)
        where TRequest : notnull =>
        GetInvoker(context).InvokeUnaryAsync<TRequest, TResponse>(request, context);

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
        where TRequest : notnull
    {
        return GetInvoker(context).InvokeUnaryAsync(wireRequest, context, mapRequest, mapResponse);
    }

    /// <summary>
    /// Starts an application server stream using this call's request services and authenticated principal.
    /// Dispose the returned invocation after enumerating it.
    /// </summary>
    /// <typeparam name="TRequest">The application stream-handler request type.</typeparam>
    /// <typeparam name="TItem">The application stream item type.</typeparam>
    /// <param name="context">The grpc-dotnet call context.</param>
    /// <param name="request">The application request to dispatch.</param>
    /// <returns>The accepted stream invocation.</returns>
    /// <exception cref="RpcException">The registered translator's representation of a failed stream startup.</exception>
    public static Task<StreamHandlerInvocation<TItem>> InvokeElarionStreamAsync<TRequest, TItem>(
        this ServerCallContext context,
        TRequest request)
        where TRequest : notnull =>
        GetStreamInvoker(context).InvokeServerStreamingAsync<TRequest, TItem>(request, context);

    /// <summary>
    /// Maps and writes a server stream using this call's request services and authenticated principal.
    /// </summary>
    /// <typeparam name="TWireRequest">The generated protobuf request type.</typeparam>
    /// <typeparam name="TWireItem">The generated protobuf response item type.</typeparam>
    /// <typeparam name="TRequest">The application stream-handler request type.</typeparam>
    /// <typeparam name="TItem">The application stream item type.</typeparam>
    /// <param name="context">The grpc-dotnet call context.</param>
    /// <param name="wireRequest">The generated protobuf request.</param>
    /// <param name="responseStream">The gRPC response stream writer.</param>
    /// <param name="mapRequest">Maps the protobuf request into the application request.</param>
    /// <param name="mapItem">Maps an application stream item into the protobuf response item.</param>
    /// <returns>A task that completes when the stream ends, faults, or is cancelled.</returns>
    /// <exception cref="RpcException">The registered translator's representation of a failed stream startup.</exception>
    public static Task InvokeElarionStreamAsync<TWireRequest, TWireItem, TRequest, TItem>(
        this ServerCallContext context,
        TWireRequest wireRequest,
        IServerStreamWriter<TWireItem> responseStream,
        Func<TWireRequest, TRequest> mapRequest,
        Func<TItem, TWireItem> mapItem)
        where TRequest : notnull
    {
        return GetStreamInvoker(context).InvokeServerStreamingAsync(
            wireRequest,
            responseStream,
            context,
            mapRequest,
            mapItem);
    }

    private static GrpcHandlerInvoker GetInvoker(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetHttpContext().RequestServices.GetRequiredService<GrpcHandlerInvoker>();
    }

    private static GrpcStreamHandlerInvoker GetStreamInvoker(ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.GetHttpContext().RequestServices.GetRequiredService<GrpcStreamHandlerInvoker>();
    }
}
