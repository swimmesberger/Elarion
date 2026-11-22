using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Elarion.Tests.Generators;

/// <summary>
/// The MSBuild <c>RootNamespace</c> a generator driver passes on, so a test compilation with a unique assembly name
/// still gets its generated statics (<c>ElarionPermissions</c>, <c>ElarionFeatureFlags</c>) in the namespace its source
/// uses.
/// </summary>
internal sealed class RootNamespaceOptions(string rootNamespace) : AnalyzerConfigOptionsProvider {
    private static readonly AnalyzerConfigOptions None = new Options(new Dictionary<string, string>());

    public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(new Dictionary<string, string> {
        ["build_property.RootNamespace"] = rootNamespace
    });

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) {
        return None;
    }

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) {
        return None;
    }

    private sealed class Options(Dictionary<string, string> values) : AnalyzerConfigOptions {
        public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) {
            return values.TryGetValue(key, out value);
        }
    }
}
