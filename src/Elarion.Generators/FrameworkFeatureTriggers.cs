using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared trigger detection for assembly-level framework feature generators.
/// </summary>
internal static class FrameworkFeatureTriggers
{
    private const string FullFrameworkAttributeMetadataName =
        "Elarion.Abstractions.UseElarionAttribute";

    public static bool HasAssemblyTrigger(Compilation compilation, string specificAttributeMetadataName) =>
        compilation.Assembly.GetAttributes().Any(attribute => {
            var attributeName = attribute.AttributeClass?.ToDisplayString();
            // Note 31: Each generator can be enabled directly, or indirectly through the full-framework opt-in attribute.
            return attributeName == specificAttributeMetadataName ||
                   attributeName == FullFrameworkAttributeMetadataName;
        });
}
