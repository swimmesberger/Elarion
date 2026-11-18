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
/// Typst rejected the document: a template or data defect. <see cref="Diagnostics"/> holds Typst's error output.
/// </summary>
public sealed class TypstCompilationException : TypstException {
    /// <summary>Creates the exception for a rejected CLI compilation.</summary>
    public TypstCompilationException(string message, int exitCode, string diagnostics) : base(message) {
        ExitCode = exitCode;
        Diagnostics = diagnostics;
        Errors = [];
    }

    /// <summary>Creates the exception for a rejected in-process compilation, which reports structured diagnostics.</summary>
    public TypstCompilationException(string message, IReadOnlyList<TypstDiagnostic> errors) : base(message) {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors;
        Diagnostics = string.Join(Environment.NewLine, errors.Select(error => error.ToString()));
    }

    /// <summary>The process exit code, or <see langword="null"/> when the document was compiled in-process.</summary>
    public int? ExitCode { get; }

    /// <summary>Typst's diagnostics as text (for the CLI: standard error, then standard output).</summary>
    public string Diagnostics { get; }

    /// <summary>
    /// The diagnostics as structured entries with file, line, and column. Only the in-process engine reports them; the
    /// CLI engine leaves the list empty and the text in <see cref="Diagnostics"/>.
    /// </summary>
    public IReadOnlyList<TypstDiagnostic> Errors { get; }
}

/// <summary>One Typst error or warning with its location.</summary>
public sealed record TypstDiagnostic {
    /// <summary><c>error</c> or <c>warning</c>.</summary>
    public required string Severity { get; init; }

    /// <summary>The message.</summary>
    public required string Message { get; init; }

    /// <summary>The file, such as <c>/main.typ</c> or <c>@local/acme-documents:1.0.0/invoice.typ</c>; null when detached.</summary>
    public string? File { get; init; }

    /// <summary>One-based line, when known.</summary>
    public int? Line { get; init; }

    /// <summary>One-based column, when known.</summary>
    public int? Column { get; init; }

    /// <summary>Typst's hints for fixing the problem.</summary>
    public IReadOnlyList<string> Hints { get; init; } = [];

    /// <summary>The call trace leading to the problem, innermost first.</summary>
    public IReadOnlyList<string> Trace { get; init; } = [];

    /// <inheritdoc />
    public override string ToString() {
        var location = File is null ? "" : Line is null ? $" ({File})" : $" ({File}:{Line}:{Column})";
        var text = $"{Severity}: {Message}{location}";
        foreach (var hint in Hints) text += $"{System.Environment.NewLine}  hint: {hint}";
        foreach (var point in Trace) text += $"{System.Environment.NewLine}  at {point}";
        return text;
    }
}

/// <summary>
/// A compilation exceeded its timeout. The CLI engine kills the process; an in-process engine can only abandon the
/// compilation, which then runs to its end in the background.
/// </summary>
public sealed class TypstTimeoutException : TypstException {
    /// <summary>Creates the exception.</summary>
    public TypstTimeoutException(string message, TimeSpan timeout) : base(message) => Timeout = timeout;

    /// <summary>The timeout that was exceeded.</summary>
    public TimeSpan Timeout { get; }
}
