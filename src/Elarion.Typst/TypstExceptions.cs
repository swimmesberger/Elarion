namespace Elarion.Typst;

/// <summary>
/// The base of every failure <see cref="ITypstCompiler"/> raises. Typst's diagnostics quote server paths and template
/// internals: log them, and translate the exception into a generic <c>AppError</c> at the handler boundary rather
/// than returning its message to a caller.
/// </summary>
public abstract class TypstException : Exception {
    /// <summary>Creates the exception.</summary>
    protected TypstException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// The Typst executable cannot be started (not bundled, not on <c>PATH</c>, not executable): an environment problem,
/// not a document defect.
/// </summary>
public sealed class TypstUnavailableException : TypstException {
    /// <summary>Creates the exception.</summary>
    public TypstUnavailableException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// Typst rejected the document (exit code other than zero): a template or data defect. <see cref="Diagnostics"/> holds
/// Typst's error output.
/// </summary>
public sealed class TypstCompilationException : TypstException {
    /// <summary>Creates the exception.</summary>
    public TypstCompilationException(string message, int exitCode, string diagnostics) : base(message) {
        ExitCode = exitCode;
        Diagnostics = diagnostics;
    }

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Typst's diagnostics (standard error, then standard output).</summary>
    public string Diagnostics { get; }
}

/// <summary>A compilation exceeded its timeout and its process was killed.</summary>
public sealed class TypstTimeoutException : TypstException {
    /// <summary>Creates the exception.</summary>
    public TypstTimeoutException(string message, TimeSpan timeout) : base(message) => Timeout = timeout;

    /// <summary>The timeout that was exceeded.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>
/// Typst could not find a package the document imports, such as the application's own
/// <c>@local/acme-documents:1.0.0</c>: a deployment problem (the package was not bundled or not copied), not a template
/// defect. Listing the package in <see cref="TypstOptions.RequiredPackages"/> reports it at startup instead.
/// </summary>
public sealed class TypstPackageNotFoundException : TypstException {
    /// <summary>Creates the exception.</summary>
    public TypstPackageNotFoundException(string message, string package, int exitCode, string diagnostics) : base(message) {
        Package = package;
        ExitCode = exitCode;
        Diagnostics = diagnostics;
    }

    /// <summary>The package Typst searched for, as Typst names it (<c>@local/acme-documents:1.0.0</c>).</summary>
    public string Package { get; }

    /// <summary>The process exit code.</summary>
    public int ExitCode { get; }

    /// <summary>Typst's diagnostics (standard error, then standard output).</summary>
    public string Diagnostics { get; }
}
