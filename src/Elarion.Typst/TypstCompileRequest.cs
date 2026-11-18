namespace Elarion.Typst;

/// <summary>One document to compile: what to render, its data, its files, and per-document overrides.</summary>
/// <example>
/// <code>
/// await using var pdf = await typst.CompilePdfAsync(new TypstCompileRequest {
///     Source = TypstSource.Package("@local/acme-documents:1.0.0", "invoice"),
///     Data = TypstData.FromValue(document, DocumentJsonContext.Default.InvoiceDocument),
///     Assets = [new TypstAsset("logo.png", logoBytes)],
///     Inputs = new Dictionary&lt;string, string&gt; { ["lang"] = "de" },
/// }, cancellationToken);
/// </code>
/// </example>
public sealed record TypstCompileRequest {
    /// <summary>What to render.</summary>
    public required TypstSource Source { get; init; }

    /// <summary>The template's model, written to <c>/data.json</c>. <see langword="null"/> writes no data file.</summary>
    public TypstData? Data { get; init; }

    /// <summary>Files written to <c>/assets/</c>. Names must be unique (ordinal).</summary>
    public IReadOnlyList<TypstAsset> Assets { get; init; } = [];

    /// <summary>
    /// Short string values visible as <c>sys.inputs</c> (<c>--input key=value</c>), such as a language code. They travel
    /// on the command line, which operating systems limit (to 32 KB on Windows), so the document's data belongs in
    /// <see cref="Data"/>. Keys must be non-empty and contain no <c>=</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// The document's creation date (<c>--creation-timestamp</c>), which Typst writes into the PDF metadata and uses
    /// for <c>datetime.today()</c>. Set it for byte-reproducible output; <see langword="null"/> uses the current time.
    /// </summary>
    public DateTimeOffset? CreationTimestamp { get; init; }

    /// <summary>Overrides <see cref="TypstOptions.PdfStandards"/> for this document; <see langword="null"/> keeps them.</summary>
    public IReadOnlyList<TypstPdfStandard>? PdfStandards { get; init; }

    /// <summary>Overrides <see cref="TypstOptions.Timeout"/> for this document; <see langword="null"/> keeps it.</summary>
    public TimeSpan? Timeout { get; init; }

    internal void Validate() {
        ArgumentNullException.ThrowIfNull(Source);
        ArgumentNullException.ThrowIfNull(Assets);
        ArgumentNullException.ThrowIfNull(Inputs);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in Assets) {
            ArgumentNullException.ThrowIfNull(asset, nameof(Assets));
            if (!names.Add(asset.Name))
                throw new ArgumentException($"Typst asset '{asset.Name}' appears more than once.", nameof(Assets));
        }

        foreach (var (key, value) in Inputs) {
            if (string.IsNullOrEmpty(key) || key.Contains('=', StringComparison.Ordinal))
                throw new ArgumentException($"Typst input key '{key}' must be non-empty and contain no '='.", nameof(Inputs));
            ArgumentNullException.ThrowIfNull(value, nameof(Inputs));
        }

        if (Timeout is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), timeout, "The Typst timeout must be positive.");
    }
}
