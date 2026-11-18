namespace Elarion.Typst;

/// <summary>
/// Typst rendering configuration, set through <c>AddElarionTypst(options => …)</c>. Every path is optional: the
/// defaults find the toolchain the <c>Elarion.Typst</c> build targets place next to the application.
/// </summary>
/// <remarks>
/// <para>
/// The default layout below <see cref="ToolchainDirectory"/> (<c>&lt;AppContext.BaseDirectory&gt;/typst</c>) is the
/// one the build produces:
/// </para>
/// <code>
/// typst/
///   bin/typst[.exe]                        the pinned CLI (ElarionTypstBundle)
///   packages/local/acme-documents/1.0.0/   the application's templates (ElarionTypstAssetsDirectory)
///   fonts/                                 the fonts the templates use
/// </code>
/// <para>
/// The compiler passes every setting explicitly and removes inherited <c>TYPST_*</c> and <c>SOURCE_DATE_EPOCH</c>
/// environment variables from the child process, so these options are the only configuration a compilation sees.
/// </para>
/// </remarks>
public sealed class TypstOptions {
    /// <summary>The conventional configuration section: <c>Typst</c> (<c>Typst__ExecutablePath</c> as an environment variable).</summary>
    public const string SectionName = "Typst";

    /// <summary>
    /// The Typst executable. A bare name (<c>typst</c>) is looked up on <c>PATH</c>; anything with a directory
    /// separator is used as given. Unset: the bundled <c>bin/typst</c> below <see cref="ToolchainDirectory"/> when
    /// it exists, otherwise <c>typst</c> on <c>PATH</c>.
    /// </summary>
    public string? ExecutablePath { get; set; }

    /// <summary>
    /// The directory holding <c>bin/</c>, <c>packages/</c>, and <c>fonts/</c>. Unset:
    /// <c>&lt;AppContext.BaseDirectory&gt;/typst</c>, where the build targets copy them.
    /// </summary>
    public string? ToolchainDirectory { get; set; }

    /// <summary>
    /// The local package directory (<c>--package-path</c>): <c>&lt;namespace&gt;/&lt;name&gt;/&lt;version&gt;/</c>
    /// trees, read-only. Typst resolves every namespace here first, so vendored <c>@preview</c> packages are found
    /// without a download. Unset: <c>packages/</c> below <see cref="ToolchainDirectory"/> when it exists.
    /// </summary>
    public string? PackagePath { get; set; }

    /// <summary>
    /// Where Typst caches downloaded <c>@preview</c> packages (<c>--package-cache-path</c>). Unset: Typst's per-user
    /// default. A server should vendor its packages into <see cref="PackagePath"/> instead of downloading them.
    /// </summary>
    public string? PackageCachePath { get; set; }

    /// <summary>
    /// Additional font directories (<c>--font-path</c>), searched recursively. <c>fonts/</c> below
    /// <see cref="ToolchainDirectory"/> is added automatically when it exists.
    /// </summary>
    public IList<string> FontPaths { get; } = [];

    /// <summary>
    /// Hides the host's installed fonts (<c>--ignore-system-fonts</c>). Default <see langword="true"/>: only Typst's
    /// embedded fonts and <see cref="FontPaths"/> are visible, so a document renders the same on a developer
    /// machine, in CI, and in the container.
    /// </summary>
    public bool IgnoreSystemFonts { get; set; } = true;

    /// <summary>
    /// The PDF standards every document must conform to (<c>--pdf-standard</c>), for example
    /// <see cref="TypstPdfStandard.PdfA2b"/> for archival documents. Empty: Typst's default (PDF 1.7). A request can
    /// override it.
    /// </summary>
    public IList<TypstPdfStandard> PdfStandards { get; } = [];

    /// <summary>
    /// Writes a tagged (accessible) PDF. Default <see langword="true"/>, Typst's own default; <see langword="false"/>
    /// passes <c>--no-pdf-tags</c> for smaller files. PDF/UA and the <c>a</c> conformance levels require tags.
    /// </summary>
    public bool PdfTags { get; set; } = true;

    /// <summary>
    /// The upper bound for one compilation, from process start to exit. The process tree is killed when it is
    /// exceeded. Default 60 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many Typst processes may run at once in this process; further requests wait (cancellably). Typst is
    /// CPU- and memory-bound and parallelizes a single compilation itself, so a burst of downloads must not start one
    /// process per request. Default: <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int MaxConcurrency { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Threads per compilation (<c>--jobs</c>). Unset: Typst's default (all CPUs). Set it to <c>1</c> together with a
    /// higher <see cref="MaxConcurrency"/> to favor throughput over the latency of a single document.
    /// </summary>
    public int? Jobs { get; set; }

    /// <summary>
    /// The directory below which every compilation gets its own workspace. Unset:
    /// <c>&lt;temp&gt;/elarion-typst</c>. It must be writable; the template tree never has to be.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// What the host does at startup when the toolchain is incomplete. Default <see cref="TypstStartupCheck.Fail"/>.
    /// </summary>
    public TypstStartupCheck StartupCheck { get; set; } = TypstStartupCheck.Fail;

    internal void Validate() {
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "TypstOptions.Timeout must be positive.");
        if (MaxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency), MaxConcurrency, "TypstOptions.MaxConcurrency must be at least 1.");
        if (Jobs is < 1)
            throw new ArgumentOutOfRangeException(nameof(Jobs), Jobs, "TypstOptions.Jobs must be at least 1 when set.");
    }
}

/// <summary>What the host does at startup when the Typst toolchain is incomplete.</summary>
public enum TypstStartupCheck {
    /// <summary>
    /// Refuse to start when the executable or a configured directory is missing, and stop the host when
    /// <c>typst --version</c> fails (it runs beside the startup, not on its path). The default: a missing toolchain
    /// surfaces on deployment, not on the first download.
    /// </summary>
    Fail,

    /// <summary>Log a warning and keep running; compilations fail with <see cref="TypstUnavailableException"/>.</summary>
    Warn,

    /// <summary>No check.</summary>
    None
}
