using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Elarion.Typst;

/// <summary>
/// A parsed Typst package specification, <c>@namespace/name:major.minor.patch</c>, and where Typst looks for it in a
/// package directory: <c>&lt;namespace&gt;/&lt;name&gt;/&lt;version&gt;/typst.toml</c>.
/// </summary>
internal readonly partial record struct TypstPackageSpec(string Namespace, string Name, string Version) {
    public static bool TryParse([NotNullWhen(true)] string? specification, out TypstPackageSpec package) {
        package = default;
        if (specification is null) return false;
        var match = Pattern().Match(specification);
        if (!match.Success) return false;
        package = new TypstPackageSpec(match.Groups["namespace"].Value, match.Groups["name"].Value, match.Groups["version"].Value);
        return true;
    }

    public static TypstPackageSpec Parse(string specification) =>
        TryParse(specification, out var package)
            ? package
            : throw new ArgumentException($"'{specification}' is not a Typst package specification.", nameof(specification));

    /// <summary>The manifest that proves the package is installed below <paramref name="packageDirectory"/>.</summary>
    public string ManifestPath(string packageDirectory) => Path.Combine(packageDirectory, Namespace, Name, Version, "typst.toml");

    public override string ToString() => $"@{Namespace}/{Name}:{Version}";

    /// <summary>Typst's wording when an import names a package it cannot find (checked against Typst 0.15).</summary>
    public static bool TryFindMissing(string diagnostics, out string specification) {
        var match = MissingPattern().Match(diagnostics);
        specification = match.Success ? match.Groups["package"].Value : "";
        return match.Success;
    }

    [GeneratedRegex(
        @"^@(?<namespace>[a-z0-9][a-z0-9-]*)/(?<name>[a-zA-Z0-9][a-zA-Z0-9_-]*):(?<version>\d+\.\d+\.\d+)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    [GeneratedRegex(
        @"package not found \(searched for (?<package>@[a-z0-9][a-z0-9-]*/[a-zA-Z0-9][a-zA-Z0-9_-]*:\d+\.\d+\.\d+)\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex MissingPattern();
}
