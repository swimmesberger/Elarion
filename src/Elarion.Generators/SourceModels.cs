using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Elarion.Generators;

/// <summary>
/// A value-equatable snapshot of a <see cref="Location"/>, so diagnostics can flow through the
/// incremental pipeline without carrying a raw <see cref="Location"/> (reference-identity, and it
/// pins the owning <see cref="SyntaxTree"/>, both of which defeat caching).
/// </summary>
internal readonly record struct LocationInfo(string? FilePath, TextSpan TextSpan, LinePositionSpan LineSpan) {
    public static LocationInfo From(Location? location) {
        if (location is null || location.SourceTree is null) return new LocationInfo(null, default, default);

        return new LocationInfo(location.SourceTree.FilePath, location.SourceSpan, location.GetLineSpan().Span);
    }

    public static LocationInfo From(ISymbol symbol) {
        return From(symbol.Locations.FirstOrDefault());
    }

    public Location? ToLocation() {
        return FilePath is null ? null : Location.Create(FilePath, TextSpan, LineSpan);
    }
}

/// <summary>
/// A value-equatable diagnostic captured during the (pure) transform stage and reported in the
/// source-output stage. Descriptors are <c>static readonly</c> singletons and the message args are an
/// <see cref="EquatableArray{T}"/>, so the record has correct structural equality.
/// </summary>
internal sealed record DiagnosticInfo(
    DiagnosticDescriptor Descriptor,
    LocationInfo Location,
    EquatableArray<string> MessageArgs) {
    public DiagnosticSeverity Severity => Descriptor.DefaultSeverity;

    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, LocationInfo location, params string[] args) {
        return new DiagnosticInfo(descriptor, location, args.ToImmutableArray());
    }

    public static DiagnosticInfo Create(DiagnosticDescriptor descriptor, Location? location, params string[] args) {
        return Create(descriptor, LocationInfo.From(location), args);
    }

    public Diagnostic ToDiagnostic() {
        return Diagnostic.Create(Descriptor, Location.ToLocation(), [.. MessageArgs]);
    }
}
