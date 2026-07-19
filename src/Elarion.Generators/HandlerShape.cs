using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Resolves a handler's transport request/response shape from its
/// <c>IHandler&lt;TRequest, TResponse&gt;</c> interface: the request type (arg 0) and the success type
/// unwrapped from <c>Result&lt;T&gt;</c> (arg 1). This replaces the older convention of scanning for nested
/// <c>Command</c>/<c>Query</c>/<c>Response</c> members, so request/response types may be declared anywhere
/// (nested or top-level) — nesting and naming carry no semantic weight.
/// </summary>
internal static class HandlerShape {
    private const string HandlerInterfaceDisplay = "Elarion.Abstractions.IHandler<TRequest, TResponse>";
    private const string StreamHandlerInterfaceDisplay = "Elarion.Abstractions.IStreamHandler<TRequest, TItem>";
    private const string ResultDisplay = "Elarion.Abstractions.Result<T>";

    public enum Failure {
        None,

        /// <summary>The type does not implement <c>IHandler&lt;,&gt;</c> (or its request arg is not a named type).</summary>
        NoHandlerInterface,

        /// <summary>The handler's response is not <c>Result&lt;T&gt;</c>, so no transport success type can be derived.</summary>
        NonResultResponse
    }

    /// <summary>
    /// Resolves the request type and the <c>Result&lt;T&gt;</c>-unwrapped success type for a transport handler.
    /// </summary>
    public static bool TryResolve(
        INamedTypeSymbol handler,
        out INamedTypeSymbol request,
        out ITypeSymbol responseInner,
        out Failure failure) {
        request = null!;
        responseInner = null!;

        var handlerInterface = FindHandlerInterface(handler);
        if (handlerInterface is null || handlerInterface.TypeArguments[0] is not INamedTypeSymbol requestType) {
            failure = Failure.NoHandlerInterface;
            return false;
        }

        if (handlerInterface.TypeArguments[1] is not INamedTypeSymbol { IsGenericType: true } response ||
            response.OriginalDefinition.ToDisplayString() != ResultDisplay) {
            failure = Failure.NonResultResponse;
            return false;
        }

        request = requestType;
        responseInner = response.TypeArguments[0];
        failure = Failure.None;
        return true;
    }

    public static INamedTypeSymbol? FindHandlerInterface(INamedTypeSymbol type) {
        foreach (var iface in type.AllInterfaces)
            if (iface.OriginalDefinition.ToDisplayString() == HandlerInterfaceDisplay)
                return iface;

        return null;
    }

    /// <summary>Finds the request/item shape of a request-driven stream handler.</summary>
    public static INamedTypeSymbol? FindStreamHandlerInterface(INamedTypeSymbol type) {
        foreach (var iface in type.AllInterfaces)
            if (iface.OriginalDefinition.ToDisplayString() == StreamHandlerInterfaceDisplay)
                return iface;

        return null;
    }

    /// <summary>Returns true if <paramref name="type"/> implements the interface with the given fully-qualified display name.</summary>
    public static bool Implements(ITypeSymbol type, string interfaceDisplay) {
        foreach (var iface in type.AllInterfaces)
            if (iface.ToDisplayString() == interfaceDisplay)
                return true;

        return false;
    }
}
