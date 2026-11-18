namespace Elarion.Typst;

/// <summary>
/// Compiles Typst documents to PDF by running the pinned Typst CLI. Register it with <c>AddElarionTypst()</c>; it is a
/// singleton and safe for concurrent use.
/// </summary>
/// <remarks>
/// <para>
/// Each compilation gets its own temporary workspace as Typst's project root: <c>/main.typ</c>, <c>/data.json</c>,
/// and <c>/assets/</c>. Templates come from the read-only package directory, so nothing is written into the
/// application's template tree and concurrent compilations cannot see each other's data. The workspace is deleted
/// when the compilation ends; the returned <see cref="TypstPdf"/> owns the output file.
/// </para>
/// <para>
/// At most <see cref="TypstOptions.MaxConcurrency"/> compilations run at once; the rest wait, cancellably. A
/// compilation that exceeds its timeout, or whose caller cancels, has its process tree killed.
/// </para>
/// <para>
/// Keep the boundary narrow: an application usually wraps this in its own document service
/// (<c>IInvoiceDocumentRenderer</c>) that builds the template model and the request, so handlers and tests do not
/// depend on Typst.
/// </para>
/// </remarks>
public interface ITypstCompiler {
    /// <summary>Compiles <paramref name="request"/> to a PDF.</summary>
    /// <exception cref="TypstUnavailableException">The Typst executable cannot be started.</exception>
    /// <exception cref="TypstCompilationException">Typst rejected the document.</exception>
    /// <exception cref="TypstTimeoutException">The compilation exceeded its timeout.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<TypstPdf> CompilePdfAsync(TypstCompileRequest request, CancellationToken cancellationToken = default);
}
