using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Elarion.Generators;

/// <summary>
/// Validates the self-typed request markers introduced by ADR-0065 (<c>IRequest&lt;TSelf, TResponse&gt;</c>,
/// <c>ICommand&lt;TSelf, TResponse&gt;</c>, <c>IQuery&lt;TSelf, TResponse&gt;</c>,
/// <c>IStreamRequest&lt;TSelf, TItem&gt;</c>): ELREQ001 when <c>TSelf</c> does not name the implementing
/// type, ELREQ002/ELREQ003 when a handler's response does not match the response the request's marker
/// declares.
/// </summary>
/// <remarks>
/// Both mistakes compile — the CRTP constraint only requires <c>TSelf</c> to implement the marker, and a
/// handler is free to pair any response with any request — but they surface at runtime as an invalid cast
/// in the inferred dispatch overloads or as failed <c>IHandler&lt;,&gt;</c> resolution. A
/// <see cref="DiagnosticAnalyzer"/> (not a generator) because nothing is emitted and the checks must cover
/// every source type, including handlers dispatched outside generated module registration (e.g. through
/// <c>ConnectionHandlerInvoker</c>). ELREQ002/ELREQ003 are warnings, not errors: a request has exactly one
/// marker-declared response, but a second handler pairing the same request with a different response
/// remains legal and reachable through the explicit-generic overloads.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RequestMarkerAnalyzer : DiagnosticAnalyzer {
    private const string RequestMarkerMetadataName = "Elarion.Abstractions.IRequest`2";
    private const string StreamRequestMarkerMetadataName = "Elarion.Abstractions.IStreamRequest`2";
    private const string HandlerInterfaceMetadataName = "Elarion.Abstractions.IHandler`2";
    private const string StreamHandlerInterfaceMetadataName = "Elarion.Abstractions.IStreamHandler`2";
    private const string ResultMetadataName = "Elarion.Abstractions.Result`1";

    private static readonly DiagnosticDescriptor SelfTypeMismatch = new(
        "ELREQ001",
        "Self-typed request marker must name the implementing type",
        "Type '{0}' implements '{1}', but its TSelf argument is '{2}', not '{0}'. A self-typed request "
        + "marker must name the implementing type itself; inferred dispatch of a '{0}' instance would fail "
        + "at runtime with an invalid cast. Declare the marker as '{3}<{0}, …>'.",
        "Elarion.Abstractions.Requests",
        DiagnosticSeverity.Error,
        true);

    private static readonly DiagnosticDescriptor HandlerResponseMismatch = new(
        "ELREQ002",
        "Handler response does not match the request's self-typed marker",
        "Handler '{0}' implements 'IHandler<{1}, {2}>', but '{1}' declares its response as '{3}' via "
        + "'{4}'. Inferred dispatch resolves 'IHandler<{1}, Result<{3}>>' and will not find this handler; "
        + "align the marker's TResponse with the handler's Result<T> (or vice versa).",
        "Elarion.Abstractions.Requests",
        DiagnosticSeverity.Warning,
        true);

    private static readonly DiagnosticDescriptor StreamHandlerItemMismatch = new(
        "ELREQ003",
        "Stream handler item does not match the request's self-typed marker",
        "Stream handler '{0}' implements 'IStreamHandler<{1}, {2}>', but '{1}' declares its item type as "
        + "'{3}' via 'IStreamRequest<{1}, {3}>'. Inferred dispatch resolves 'IStreamHandler<{1}, {3}>' and "
        + "will not find this handler; align the marker's TItem with the handler's item type (or vice versa).",
        "Elarion.Abstractions.Requests",
        DiagnosticSeverity.Warning,
        true);

    private static readonly ImmutableArray<DiagnosticDescriptor> SupportedDiagnosticsArray =
        ImmutableArray.Create(SelfTypeMismatch, HandlerResponseMismatch, StreamHandlerItemMismatch);

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => SupportedDiagnosticsArray;

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context) {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(static startContext => {
            var compilation = startContext.Compilation;
            var requestMarker = compilation.GetTypeByMetadataName(RequestMarkerMetadataName);
            if (requestMarker is null) return;

            var markers = new MarkerTypes(
                requestMarker,
                compilation.GetTypeByMetadataName(StreamRequestMarkerMetadataName),
                compilation.GetTypeByMetadataName(HandlerInterfaceMetadataName),
                compilation.GetTypeByMetadataName(StreamHandlerInterfaceMetadataName),
                compilation.GetTypeByMetadataName(ResultMetadataName));

            startContext.RegisterSymbolAction(
                symbolContext => AnalyzeNamedType(symbolContext, markers),
                SymbolKind.NamedType);
        });
    }

    private sealed record MarkerTypes(
        INamedTypeSymbol RequestMarker,
        INamedTypeSymbol? StreamRequestMarker,
        INamedTypeSymbol? HandlerInterface,
        INamedTypeSymbol? StreamHandlerInterface,
        INamedTypeSymbol? ResultType);

    private static void AnalyzeNamedType(SymbolAnalysisContext context, MarkerTypes markers) {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct or TypeKind.Interface)) return;

        foreach (var iface in type.AllInterfaces)
            if (IsMarker(iface, markers.RequestMarker))
                CheckSelfType(context, type, iface, "IRequest/ICommand/IQuery");
            else if (IsMarker(iface, markers.StreamRequestMarker))
                CheckSelfType(context, type, iface, "IStreamRequest");
            else if (IsMarker(iface, markers.HandlerInterface))
                CheckHandlerResponse(context, type, iface, markers);
            else if (IsMarker(iface, markers.StreamHandlerInterface))
                CheckStreamHandlerItem(context, type, iface, markers);
    }

    private static bool IsMarker(INamedTypeSymbol iface, INamedTypeSymbol? definition) {
        return definition is not null &&
               SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, definition);
    }

    private static void CheckSelfType(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        INamedTypeSymbol markerClosure,
        string markerDisplay) {
        var self = markerClosure.TypeArguments[0];
        // A type-parameter TSelf is the legitimate CRTP pass-through shape (e.g. ICommand<TSelf, TResponse>
        // itself, or an application-defined intermediate marker interface) — the concrete closure is
        // checked where it is finally bound.
        if (self is ITypeParameterSymbol || IsSelfOrAncestor(type, self)) return;

        context.ReportDiagnostic(Diagnostic.Create(
            SelfTypeMismatch,
            type.Locations[0],
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            markerClosure.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            self.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            markerDisplay));
    }

    private static void CheckHandlerResponse(
        SymbolAnalysisContext context,
        INamedTypeSymbol handler,
        INamedTypeSymbol handlerClosure,
        MarkerTypes markers) {
        if (handlerClosure.TypeArguments[0] is not INamedTypeSymbol request)
            return; // Open generic decorators constrain TRequest later; nothing to compare here.

        var declaredClosures = GetSelfClosures(request, markers.RequestMarker);
        if (declaredClosures.IsEmpty) return;

        // A request with several self-closures makes call-site inference ambiguous but each closure is a
        // valid contract, so the handler passes when it matches any of them.
        var response = handlerClosure.TypeArguments[1];
        if (response is INamedTypeSymbol { IsGenericType: true } named && IsMarker(named, markers.ResultType))
            foreach (var closure in declaredClosures)
                if (SymbolEqualityComparer.Default.Equals(named.TypeArguments[0], closure.TypeArguments[1]))
                    return;

        var declaringClosure = declaredClosures[0];
        var declared = declaringClosure.TypeArguments[1];

        context.ReportDiagnostic(Diagnostic.Create(
            HandlerResponseMismatch,
            handler.Locations[0],
            handler.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            request.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            response.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            declared.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            declaringClosure.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    private static void CheckStreamHandlerItem(
        SymbolAnalysisContext context,
        INamedTypeSymbol handler,
        INamedTypeSymbol handlerClosure,
        MarkerTypes markers) {
        if (handlerClosure.TypeArguments[0] is not INamedTypeSymbol request) return;

        var declaredClosures = GetSelfClosures(request, markers.StreamRequestMarker);
        if (declaredClosures.IsEmpty) return;

        var item = handlerClosure.TypeArguments[1];
        foreach (var closure in declaredClosures)
            if (SymbolEqualityComparer.Default.Equals(item, closure.TypeArguments[1]))
                return;

        var declaredItem = declaredClosures[0].TypeArguments[1];

        context.ReportDiagnostic(Diagnostic.Create(
            StreamHandlerItemMismatch,
            handler.Locations[0],
            handler.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            request.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            item.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            declaredItem.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }

    /// <summary>
    /// Finds the marker closures the request declares for <em>itself</em> — the closures inferred
    /// dispatch actually uses. Closures naming a base type are ignored: dispatching the derived instance
    /// infers the base request type, which is checked on its own declaration.
    /// </summary>
    private static ImmutableArray<INamedTypeSymbol> GetSelfClosures(
        INamedTypeSymbol request,
        INamedTypeSymbol? markerDefinition) {
        if (markerDefinition is null) return ImmutableArray<INamedTypeSymbol>.Empty;

        var closures = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        foreach (var iface in request.AllInterfaces)
            if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, markerDefinition) &&
                SymbolEqualityComparer.Default.Equals(iface.TypeArguments[0], request))
                closures.Add(iface);

        return closures.ToImmutable();
    }

    private static bool IsSelfOrAncestor(INamedTypeSymbol type, ITypeSymbol self) {
        if (SymbolEqualityComparer.Default.Equals(type, self)) return true;

        // TSelf naming a base type or implemented interface stays dispatchable: the cast in the inferred
        // overloads succeeds and resolution targets the named type's handler, which is validated at its
        // own declaration.
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            if (SymbolEqualityComparer.Default.Equals(baseType, self))
                return true;

        if (self.TypeKind == TypeKind.Interface)
            foreach (var iface in type.AllInterfaces)
                if (SymbolEqualityComparer.Default.Equals(iface, self))
                    return true;

        return false;
    }
}
