using Microsoft.CodeAnalysis.CSharp;

namespace Elarion.Generators;

/// <summary>
/// Emit-time helpers for rendering C# literals in generated source. Shared (via <c>&lt;Compile Include&gt;</c>)
/// with the bundled EF feature generators so every generator escapes attribute-supplied strings the same way.
/// </summary>
internal static class SourceLiterals {
    /// <summary>Renders a nullable string as a C# expression — a quoted, escaped literal, or <c>null</c>.</summary>
    public static string String(string? value) {
        return value is null ? "null" : SymbolDisplay.FormatLiteral(value, true);
    }
}
