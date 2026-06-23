using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

/// <summary>
/// Shared trigger detection for assembly-level framework feature generators.
/// </summary>
internal static class FrameworkFeatureTriggers
{
    private const string FullFrameworkAttributeMetadataName =
        "Elarion.Abstractions.UseElarionAttribute";

    public static bool HasAssemblyTrigger(Compilation compilation, string specificAttributeMetadataName)
    {
        // Resolve both trigger symbols once and compare by symbol identity, rather than allocating a
        // display string per assembly attribute. Each generator can be enabled directly, or indirectly
        // through the full-framework opt-in attribute.
        var specificSymbol = compilation.GetTypeByMetadataName(specificAttributeMetadataName);
        var fullFrameworkSymbol = compilation.GetTypeByMetadataName(FullFrameworkAttributeMetadataName);
        if (specificSymbol is null && fullFrameworkSymbol is null)
        {
            return false;
        }

        foreach (var attribute in compilation.Assembly.GetAttributes())
        {
            var attributeClass = attribute.AttributeClass;
            if (attributeClass is null)
            {
                continue;
            }

            if (SymbolEqualityComparer.Default.Equals(attributeClass, specificSymbol) ||
                SymbolEqualityComparer.Default.Equals(attributeClass, fullFrameworkSymbol))
            {
                return true;
            }
        }

        return false;
    }
}
