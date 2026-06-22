namespace Elarion.Abstractions;

/// <summary>The HTTP method an <see cref="HttpEndpointAttribute"/> maps a handler to.</summary>
public enum HttpVerb {
    /// <summary>HTTP GET.</summary>
    Get,
    /// <summary>HTTP POST.</summary>
    Post,
    /// <summary>HTTP PUT.</summary>
    Put,
    /// <summary>HTTP PATCH.</summary>
    Patch,
    /// <summary>HTTP DELETE.</summary>
    Delete,
}

/// <summary>
/// Marks a handler class as an HTTP endpoint, discoverable by <c>Elarion.Generators.AppModuleDiscoveryGenerator</c>,
/// which emits the matching minimal-API <c>MapGet</c>/<c>MapPost</c>/... registration through the module
/// bootstrapper. The handler must also
/// implement <see cref="IHandler{TRequest, TResponse}"/> and nest its request type as <c>Command</c> or
/// <c>Query</c> plus a <c>Response</c> type (same convention as <see cref="RpcMethodAttribute"/>).
/// </summary>
/// <remarks>
/// When the verb is omitted (the <see cref="HttpEndpointAttribute(string)"/> constructor), it is inferred from
/// the nested request type: <c>Command</c> maps to <see cref="HttpVerb.Post"/> and <c>Query</c> maps to
/// <see cref="HttpVerb.Get"/>. This type lives in <c>Elarion.Abstractions</c> and intentionally carries no
/// ASP.NET Core dependency — it is pure declarative metadata. A handler may carry both this attribute and
/// <see cref="RpcMethodAttribute"/> to be exposed over both transports.
/// </remarks>
/// <example>
/// <code>
/// [HttpEndpoint("clients/{id}")]                       // verb inferred from the nested Query -> GET
/// public sealed class GetClient : IHandler&lt;GetClient.Query, Result&lt;GetClient.Response&gt;&gt; { ... }
///
/// [HttpEndpoint(HttpVerb.Delete, "clients/{id}")]      // verb explicit
/// public sealed class DeleteClient : IHandler&lt;DeleteClient.Command, Result&lt;DeleteClient.Response&gt;&gt; { ... }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class HttpEndpointAttribute : Attribute {
    /// <summary>Maps the handler at <paramref name="route"/>, inferring the verb from the nested request type.</summary>
    /// <param name="route">The route pattern (e.g. <c>"clients/{id}"</c>).</param>
    public HttpEndpointAttribute(string route) => Route = route;

    /// <summary>Maps the handler at <paramref name="route"/> using the explicit <paramref name="verb"/>.</summary>
    /// <param name="verb">The HTTP method.</param>
    /// <param name="route">The route pattern (e.g. <c>"clients/{id}"</c>).</param>
    public HttpEndpointAttribute(HttpVerb verb, string route) {
        Verb = verb;
        HasVerb = true;
        Route = route;
    }

    /// <summary>The route pattern the handler is mapped at.</summary>
    public string Route { get; }

    /// <summary>The explicit HTTP method, or the default when <see cref="HasVerb"/> is <c>false</c>.</summary>
    public HttpVerb Verb { get; }

    /// <summary>Whether <see cref="Verb"/> was set explicitly; when <c>false</c> the verb is inferred.</summary>
    public bool HasVerb { get; }
}
