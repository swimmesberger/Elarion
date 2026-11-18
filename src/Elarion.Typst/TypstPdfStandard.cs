namespace Elarion.Typst;

/// <summary>A PDF standard Typst enforces conformance with (<c>--pdf-standard</c>).</summary>
public enum TypstPdfStandard {
    /// <summary>PDF 1.4.</summary>
    Pdf14,

    /// <summary>PDF 1.5.</summary>
    Pdf15,

    /// <summary>PDF 1.6.</summary>
    Pdf16,

    /// <summary>PDF 1.7, Typst's default.</summary>
    Pdf17,

    /// <summary>PDF 2.0.</summary>
    Pdf20,

    /// <summary>PDF/A-1b.</summary>
    PdfA1b,

    /// <summary>PDF/A-1a.</summary>
    PdfA1a,

    /// <summary>PDF/A-2b: the usual choice for archived business documents (invoices, protocols).</summary>
    PdfA2b,

    /// <summary>PDF/A-2u.</summary>
    PdfA2u,

    /// <summary>PDF/A-2a.</summary>
    PdfA2a,

    /// <summary>PDF/A-3b: PDF/A-2b plus arbitrary embedded files (for example e-invoice XML).</summary>
    PdfA3b,

    /// <summary>PDF/A-3u.</summary>
    PdfA3u,

    /// <summary>PDF/A-3a.</summary>
    PdfA3a,

    /// <summary>PDF/A-4.</summary>
    PdfA4,

    /// <summary>PDF/A-4f.</summary>
    PdfA4f,

    /// <summary>PDF/A-4e.</summary>
    PdfA4e,

    /// <summary>PDF/UA-1 (universal accessibility).</summary>
    PdfUa1
}

internal static class TypstPdfStandardExtensions {
    public static string ToCliValue(this TypstPdfStandard standard) => standard switch {
        TypstPdfStandard.Pdf14 => "1.4",
        TypstPdfStandard.Pdf15 => "1.5",
        TypstPdfStandard.Pdf16 => "1.6",
        TypstPdfStandard.Pdf17 => "1.7",
        TypstPdfStandard.Pdf20 => "2.0",
        TypstPdfStandard.PdfA1b => "a-1b",
        TypstPdfStandard.PdfA1a => "a-1a",
        TypstPdfStandard.PdfA2b => "a-2b",
        TypstPdfStandard.PdfA2u => "a-2u",
        TypstPdfStandard.PdfA2a => "a-2a",
        TypstPdfStandard.PdfA3b => "a-3b",
        TypstPdfStandard.PdfA3u => "a-3u",
        TypstPdfStandard.PdfA3a => "a-3a",
        TypstPdfStandard.PdfA4 => "a-4",
        TypstPdfStandard.PdfA4f => "a-4f",
        TypstPdfStandard.PdfA4e => "a-4e",
        TypstPdfStandard.PdfUa1 => "ua-1",
        _ => throw new ArgumentOutOfRangeException(nameof(standard), standard, "Unknown PDF standard.")
    };
}
