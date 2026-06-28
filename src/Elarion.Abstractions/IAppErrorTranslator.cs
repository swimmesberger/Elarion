namespace Elarion.Abstractions;

/// <summary>
/// Translates the framework's transport-agnostic <see cref="AppError"/> into a transport's wire error
/// representation — a JSON-RPC error, an HTTP status, a gRPC <c>Status</c>, a CLI exit code, etc. Each
/// transport supplies the <typeparamref name="TError"/> it needs; a handler's <c>Result</c> failure is run
/// through the registered translator before it leaves the transport boundary.
/// </summary>
/// <typeparam name="TError">The transport's wire error type.</typeparam>
/// <remarks>
/// The JSON-RPC transport ships and uses a default translator (mapping <see cref="ErrorKind"/> to JSON-RPC
/// codes); registering your own <c>IAppErrorTranslator&lt;RpcError&gt;</c> overrides those codes. A new
/// transport author implements this for their wire type and applies it where they translate a failed
/// <see cref="Result{T}"/>.
/// </remarks>
public interface IAppErrorTranslator<TError> {
    /// <summary>Translates <paramref name="error"/> into the transport's wire error representation.</summary>
    TError Translate(AppError error);
}
