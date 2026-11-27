using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Elarion.Typst;

/// <summary>
/// What a compilation renders: a function of a Typst package (the recommended shape) or a complete main file.
/// </summary>
/// <remarks>
/// <para>
/// Every compilation runs in a fresh workspace that is Typst's project root (<c>--root</c>). It holds
/// <c>/main.typ</c>, <c>/data.json</c> (<see cref="TypstCompileRequest.Data"/>), and
/// <c>/assets/&lt;name&gt;</c> (<see cref="TypstCompileRequest.Assets"/>). Typst only reads below that root and the
/// package directory, so templates live in a <b>package</b>: read-only, versioned, and shared by every workspace.
/// </para>
/// <para>
/// A package cannot read the project root's files, so <see cref="Package"/> generates a main file that reads them
/// and hands them over: the template receives the parsed data and the asset bytes and never touches the file system.
/// </para>
/// </remarks>
public sealed partial class TypstSource {
    private readonly string? _mainFile;
    private readonly string? _packageSpec;
    private readonly string? _function;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _arguments;

    private TypstSource(
        string description,
        string? mainFile,
        string? packageSpec,
        string? function,
        IReadOnlyList<KeyValuePair<string, string>>? arguments = null) {
        Description = description;
        _mainFile = mainFile;
        _packageSpec = packageSpec;
        _function = function;
        _arguments = arguments ?? [];
    }

    /// <summary>A short label for logs and spans: the package function, or <c>main.typ</c>.</summary>
    public string Description { get; }

    /// <summary>
    /// Calls <paramref name="function"/> of <paramref name="package"/> with the request's data and assets:
    /// <c>#function(data, assets: (name: bytes, …))</c>. <c>data</c> is the parsed <c>data.json</c>, or
    /// <c>none</c> when the request has no data; <c>assets</c> maps each asset name to its bytes, ready for
    /// <c>image(assets.at("logo.png"))</c>. Further named arguments are added with
    /// <see cref="WithArgument(string, string)"/> and its overloads.
    /// </summary>
    /// <param name="package">A package specification such as <c>@local/acme-documents:1.0.0</c>.</param>
    /// <param name="function">The entry-point function the package exports, such as <c>invoice</c>.</param>
    /// <example>
    /// <code>
    /// // packages/local/acme-documents/1.0.0/lib.typ
    /// #let invoice(data, assets: (:)) = { … }
    ///
    /// var request = new TypstCompileRequest {
    ///     Source = TypstSource.Package("@local/acme-documents:1.0.0", "invoice"),
    ///     Data = TypstData.FromValue(model, DocumentJsonContext.Default.InvoiceDocument),
    /// };
    /// </code>
    /// </example>
    public static TypstSource Package(string package, string function) {
        ArgumentException.ThrowIfNullOrWhiteSpace(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(function);
        if (!TypstPackageSpec.TryParse(package, out _))
            throw new ArgumentException(
                $"'{package}' is not a Typst package specification of the form @namespace/name:major.minor.patch.",
                nameof(package));
        if (!IdentifierPattern().IsMatch(function))
            throw new ArgumentException($"'{function}' is not a Typst identifier.", nameof(function));

        return new TypstSource($"{package}:{function}", mainFile: null, package, function);
    }

    /// <summary>
    /// Uses <paramref name="mainFile"/> verbatim as <c>/main.typ</c>. It reads the request's files itself, by their
    /// absolute project paths: <c>json("/data.json")</c> and <c>read("/assets/logo.png", encoding: none)</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// TypstSource.Main("""
    ///     #import "@local/acme-documents:1.0.0": letter
    ///     #letter(json("/data.json"), lang: sys.inputs.at("lang", default: "en"))
    ///     """);
    /// </code>
    /// </example>
    public static TypstSource Main(string mainFile) {
        ArgumentException.ThrowIfNullOrWhiteSpace(mainFile);
        return new TypstSource(TypstWorkspace.MainFileName, mainFile, packageSpec: null, function: null);
    }

    /// <summary>
    /// Adds a named string argument to a <see cref="Package"/> call: <c>#function(data, name: "value", assets: …)</c>.
    /// The value is written as an escaped Typst string literal, so any text is safe.
    /// </summary>
    /// <example>
    /// <code>
    /// // #let render-report(data, kind: "test-report", lang: "en", assets: (:)) = …
    /// TypstSource.Package("@local/acme-reports:1.0.0", "render-report")
    ///     .WithArgument("kind", report.Kind)
    ///     .WithArgument("lang", language.Code);
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">This source is a <see cref="Main"/> file.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a Typst identifier, is <c>assets</c>, or was already added.
    /// </exception>
    public TypstSource WithArgument(string name, string value) {
        ArgumentNullException.ThrowIfNull(value);
        return WithLiteral(name, StringLiteral(value));
    }

    /// <summary>Adds a named boolean argument to a <see cref="Package"/> call.</summary>
    /// <inheritdoc cref="WithArgument(string, string)" path="/exception"/>
    public TypstSource WithArgument(string name, bool value) => WithLiteral(name, value ? "true" : "false");

    /// <summary>Adds a named integer argument to a <see cref="Package"/> call.</summary>
    /// <inheritdoc cref="WithArgument(string, string)" path="/exception"/>
    public TypstSource WithArgument(string name, long value) =>
        WithLiteral(name, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Adds a named argument to a <see cref="Package"/> call as a Typst <c>decimal</c>, so an amount keeps its exact
    /// value (a JSON number would arrive as a float).
    /// </summary>
    /// <inheritdoc cref="WithArgument(string, string)" path="/exception"/>
    public TypstSource WithArgument(string name, decimal value) =>
        WithLiteral(name, $"decimal(\"{value.ToString(CultureInfo.InvariantCulture)}\")");

    private TypstSource WithLiteral(string name, string literal) {
        if (_mainFile is not null)
            throw new InvalidOperationException(
                "Arguments apply to TypstSource.Package calls; a TypstSource.Main file passes its own arguments.");
        if (string.IsNullOrEmpty(name) || !IdentifierPattern().IsMatch(name))
            throw new ArgumentException($"'{name}' is not a Typst identifier.", nameof(name));
        if (name == "assets")
            throw new ArgumentException("'assets' is passed by every package call and cannot be set as an argument.", nameof(name));
        foreach (var (existing, _) in _arguments) {
            if (existing == name) throw new ArgumentException($"The argument '{name}' was already added.", nameof(name));
        }

        return new TypstSource(Description, mainFile: null, _packageSpec, _function, [.. _arguments, new(name, literal)]);
    }

    /// <summary>A Typst string literal: backslash, quote, and control characters escaped.</summary>
    internal static string StringLiteral(string value) {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var ch in value) {
            switch (ch) {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(ch)) {
                        builder.Append("\\u{").Append(((int)ch).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    } else {
                        builder.Append(ch);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    /// <summary>The main file for a request: a package call needs the request's asset names.</summary>
    internal string RenderMainFile(bool hasData, IReadOnlyList<TypstAsset> assets) {
        if (_mainFile is not null) return _mainFile;

        var builder = new StringBuilder();
        builder.Append("#import \"").Append(_packageSpec).Append("\": ").Append(_function).Append('\n');
        builder.Append('#').Append(_function).Append('(');
        builder.Append(hasData ? $"json(\"/{TypstWorkspace.DataFileName}\")" : "none");
        foreach (var (name, literal) in _arguments) {
            builder.Append(", ").Append(name).Append(": ").Append(literal);
        }

        builder.Append(", assets: (");
        if (assets.Count == 0) {
            builder.Append(':');
        } else {
            foreach (var asset in assets) {
                // Asset names are restricted to [A-Za-z0-9._-], so they need no escaping in a Typst string.
                builder.Append('"').Append(asset.Name).Append("\": read(\"/")
                    .Append(TypstWorkspace.AssetsDirectoryName).Append('/').Append(asset.Name)
                    .Append("\", encoding: none), ");
            }
        }

        builder.Append("))\n");
        return builder.ToString();
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}
