namespace Elarion.Abstractions;

/// <summary>
/// Marks a handler class as an RPC method, discoverable by the <c>RpcDispatcher</c>.
/// The handler must also implement <see cref="IHandler{TRequest, TResponse}"/>.
/// </summary>
/// <example>
/// <code>
/// [RpcMethod("clients.create")]
/// public sealed class CreateClient(...) : IHandler&lt;CreateClient.Command, Result&lt;CreateClient.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RpcMethodAttribute(string methodName) : Attribute {
    /// <summary>The JSON-RPC method name (e.g., "clients.create").</summary>
    public string MethodName { get; } = methodName;
}
