using System.ComponentModel;
using System.Diagnostics;

namespace Elarion.Typst;

/// <summary>
/// Where this process finds the Typst executable, the package directory, and the fonts - resolved once from
/// <see cref="TypstOptions"/>. Registered as a singleton by <c>AddElarionTypst()</c>.
/// </summary>
/// <remarks>
/// Test suites use it to skip Typst-backed tests on a machine without the toolchain:
/// <code>
/// var toolchain = new TypstToolchain(new TypstOptions());
/// Assert.SkipUnless(toolchain.ExecutableExists, $"Typst is not available at '{toolchain.ExecutablePath}'.");
/// </code>
/// </remarks>
public sealed class TypstToolchain {
    private readonly Func<string, bool> _fileExists;

    /// <summary>Resolves the toolchain from <paramref name="options"/>.</summary>
    public TypstToolchain(TypstOptions options)
        : this(options, Environment.GetEnvironmentVariable("PATH"), AppContext.BaseDirectory, File.Exists, Directory.Exists) { }

    internal TypstToolchain(
        TypstOptions options,
        string? pathVariable,
        string baseDirectory,
        Func<string, bool> fileExists,
        Func<string, bool> directoryExists) {
        ArgumentNullException.ThrowIfNull(options);
        _fileExists = fileExists;

        ToolchainDirectory = Path.GetFullPath(options.ToolchainDirectory ?? Path.Combine(baseDirectory, "typst"));

        var bundled = Path.Combine(ToolchainDirectory, "bin", OperatingSystem.IsWindows() ? "typst.exe" : "typst");
        var configured = options.ExecutablePath;
        if (string.IsNullOrWhiteSpace(configured)) {
            configured = fileExists(bundled) ? bundled : "typst";
        }

        ConfiguredExecutable = configured;
        var resolved = ResolveExecutable(configured, pathVariable, fileExists);
        ExecutableExists = resolved is not null;
        ExecutablePath = resolved ?? configured;

        var packages = Path.Combine(ToolchainDirectory, "packages");
        PackagePath = options.PackagePath is { Length: > 0 } packagePath
            ? Path.GetFullPath(packagePath)
            : directoryExists(packages) ? packages : null;

        PackageCachePath = options.PackageCachePath is { Length: > 0 } cachePath ? Path.GetFullPath(cachePath) : null;

        var fonts = new List<string>();
        foreach (var fontPath in options.FontPaths) {
            if (!string.IsNullOrWhiteSpace(fontPath)) fonts.Add(Path.GetFullPath(fontPath));
        }

        var bundledFonts = Path.Combine(ToolchainDirectory, "fonts");
        if (directoryExists(bundledFonts) && !fonts.Contains(bundledFonts, StringComparer.Ordinal)) fonts.Add(bundledFonts);
        FontPaths = fonts;
    }

    /// <summary>The directory holding the bundled <c>bin/</c>, <c>packages/</c>, and <c>fonts/</c>.</summary>
    public string ToolchainDirectory { get; }

    /// <summary>
    /// The executable to start: the resolved file when <see cref="ExecutableExists"/>, otherwise the configured
    /// value (for error messages).
    /// </summary>
    public string ExecutablePath { get; }

    /// <summary>Whether <see cref="ExecutablePath"/> names an existing file (after a <c>PATH</c> lookup for a bare name).</summary>
    public bool ExecutableExists { get; }

    /// <summary>The package directory passed as <c>--package-path</c>, or <see langword="null"/>.</summary>
    public string? PackagePath { get; }

    /// <summary>The package cache passed as <c>--package-cache-path</c>, or <see langword="null"/>.</summary>
    public string? PackageCachePath { get; }

    /// <summary>The font directories passed as <c>--font-path</c>.</summary>
    public IReadOnlyList<string> FontPaths { get; }

    internal string ConfiguredExecutable { get; }

    /// <summary>
    /// Whether <paramref name="package"/> is installed in <see cref="PackagePath"/>: whether its
    /// <c>&lt;namespace&gt;/&lt;name&gt;/&lt;version&gt;/typst.toml</c> exists. A render-time probe for the
    /// application's own package, so a missing deployment can be told apart from a broken template before compiling.
    /// </summary>
    /// <param name="package">A package specification such as <c>@local/acme-documents:1.0.0</c>.</param>
    /// <returns>
    /// <see langword="false"/> when the package is missing or no package directory is configured or bundled. A
    /// <c>@preview</c> package Typst would download on first use counts as missing: a server vendors it.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="package"/> is not a package specification.</exception>
    /// <example>
    /// <code>
    /// if (!toolchain.HasPackage("@local/acme-documents:1.0.0"))
    ///     return AppError.BusinessRule("The document templates are not installed.", code: "document.templates-missing");
    /// </code>
    /// </example>
    public bool HasPackage(string package) {
        if (!TypstPackageSpec.TryParse(package, out var spec))
            throw new ArgumentException(
                $"'{package}' is not a Typst package specification of the form @namespace/name:major.minor.patch.", nameof(package));
        return PackagePath is { } packagePath && _fileExists(spec.ManifestPath(packagePath));
    }

    /// <summary>Why <paramref name="package"/> is missing, for the startup check.</summary>
    internal string MissingPackageMessage(string package) =>
        PackagePath is { } packagePath
            ? $"The required Typst package '{package}' is not installed: '{TypstPackageSpec.Parse(package).ManifestPath(packagePath)}' does not exist."
            : $"The required Typst package '{package}' is not installed: no package directory is configured (TypstOptions.PackagePath) "
              + $"and '{Path.Combine(ToolchainDirectory, "packages")}' does not exist. Bundle the package with ElarionTypstAssetsDirectory.";

    /// <summary>
    /// Runs <c>typst --version</c> and returns its output, such as <c>typst 0.15.1 (…)</c>.
    /// </summary>
    /// <exception cref="TypstUnavailableException">The executable cannot be started or exits with an error.</exception>
    public async Task<string> GetVersionAsync(CancellationToken cancellationToken = default) {
        var startInfo = new ProcessStartInfo(ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        startInfo.ArgumentList.Add("--version");

        TypstRun run;
        try {
            run = await TypstProcess.RunAsync(startInfo, cancellationToken).ConfigureAwait(false);
        } catch (Win32Exception ex) {
            throw new TypstUnavailableException(UnavailableMessage(), ex);
        }

        if (run.ExitCode != 0)
            throw new TypstUnavailableException(
                $"'{ExecutablePath} --version' exited with code {run.ExitCode}: {run.StandardError.Trim()}");
        return run.StandardOutput.Trim();
    }

    internal string UnavailableMessage() =>
        ExecutableExists
            ? $"The Typst executable '{ExecutablePath}' could not be started."
            : $"The Typst executable '{ConfiguredExecutable}' was not found. Reference Elarion.Typst from the executable so the build "
              + "bundles it (ElarionTypstBundle), put typst on PATH, or set TypstOptions.ExecutablePath.";

    /// <summary>
    /// Mirrors how <see cref="Process.Start()"/> resolves a file name: a value with a directory separator is a path,
    /// a bare name is searched on <c>PATH</c> (with its <c>.exe</c> sibling, for Windows).
    /// </summary>
    internal static string? ResolveExecutable(string executable, string? pathVariable, Func<string, bool> fileExists) {
        if (string.IsNullOrWhiteSpace(executable)) return null;

        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            return Candidate(Path.GetFullPath(executable), fileExists);

        foreach (var directory in (pathVariable ?? string.Empty).Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (Candidate(Path.Combine(directory, executable), fileExists) is { } found) return found;
        }

        return null;
    }

    private static string? Candidate(string path, Func<string, bool> fileExists) {
        if (fileExists(path)) return path;
        var windows = path + ".exe";
        return fileExists(windows) ? windows : null;
    }
}
