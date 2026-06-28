using Elarion.Abstractions;

namespace Elarion.JsonRpc;

/// <summary>
/// The default <see cref="IAppErrorTranslator{TError}"/> for the JSON-RPC transport: maps an
/// <see cref="AppError"/> to an <see cref="RpcError"/> using <see cref="AppErrorMapper"/>. Used by
/// <see cref="RpcDispatcherExtensions.MapHandler{TRequest,TResponse}"/> unless a host registers its own
/// <c>IAppErrorTranslator&lt;RpcError&gt;</c> to override the codes.
/// </summary>
public sealed class JsonRpcAppErrorTranslator : IAppErrorTranslator<RpcError> {
    /// <summary>The shared default instance.</summary>
    public static JsonRpcAppErrorTranslator Default { get; } = new();

    /// <inheritdoc />
    public RpcError Translate(AppError error) => AppErrorMapper.ToRpcError(error);
}
