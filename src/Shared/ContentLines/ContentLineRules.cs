namespace Elarion.ContentLines;

/// <summary>
/// What differs between the content-line formats that share this layer (ADR-0109, ADR-0110): how much indentation a
/// continuation line loses, and which vCard-only grammar applies. The layer knows no property names; a package that
/// needs a limit per property passes <see cref="LineLimitFor"/>.
/// </summary>
internal sealed class ContentLineRules {
    /// <summary>
    /// The iCalendar rule set (RFC 5545): one space or tab removed per fold, no groups, no vCard 2.1 grammar, and a
    /// byte-order mark at the start of a line, as calendars concatenated from several exports carry.
    /// </summary>
    public static ContentLineRules ICalendar(int maxLineBytes) => new() { MaxLineBytes = maxLineBytes, SkipByteOrderMarks = true };

    /// <summary>
    /// The vCard rule set (RFC 2425, 2426, 6350 and vCard 2.1): every leading space and tab of a continuation removed,
    /// groups, bare parameters, <c>CHARSET</c>, quoted-printable soft breaks, Base64 ending at a blank line, and
    /// <c>#</c> comment lines, and a byte-order mark at the start of a line.
    /// </summary>
    public static ContentLineRules VCard(int maxLineBytes, Func<ReadOnlySpan<byte>, int>? lineLimitFor = null) => new() {
        MaxLineBytes = maxLineBytes,
        UnfoldAllWhitespace = true,
        AllowGroups = true,
        AllowBareParameters = true,
        UseCharset = true,
        QuotedPrintableSoftBreaks = true,
        Base64UntilBlankLine = true,
        SkipCommentLines = true,
        SkipByteOrderMarks = true,
        LineLimitFor = lineLimitFor,
    };

    /// <summary>The longest unfolded line kept, in bytes; a longer one is consumed and reported as too long.</summary>
    public int MaxLineBytes { get; init; }

    /// <summary>A continuation line loses every leading space and tab (vCard), not just the first one (iCalendar).</summary>
    public bool UnfoldAllWhitespace { get; init; }

    /// <summary>A name may carry a group prefix (<c>item1.TEL</c>).</summary>
    public bool AllowGroups { get; init; }

    /// <summary>A parameter without <c>=</c> is a vCard 2.1 bare parameter (<c>TEL;WORK;VOICE</c>).</summary>
    public bool AllowBareParameters { get; init; }

    /// <summary>A <c>CHARSET</c> parameter is the first step of the decoding ladder.</summary>
    public bool UseCharset { get; init; }

    /// <summary>A quoted-printable value continues on the next physical line after a trailing <c>=</c>.</summary>
    public bool QuotedPrintableSoftBreaks { get; init; }

    /// <summary>A Base64 value continues on unindented lines up to a blank line or the next property.</summary>
    public bool Base64UntilBlankLine { get; init; }

    /// <summary>Lines starting with <c>#</c> are skipped.</summary>
    public bool SkipCommentLines { get; init; }

    /// <summary>
    /// A byte-order mark (U+FEFF, as UTF-8) at the start of a physical line is nothing: files concatenated from several
    /// exports carry one before each later <c>BEGIN</c>, which would otherwise not read as a line. On for both formats.
    /// </summary>
    public bool SkipByteOrderMarks { get; init; }

    /// <summary>
    /// The line limit of one logical line, given the bytes of its first physical line; <see langword="null"/> uses
    /// <see cref="MaxLineBytes"/> for every line.
    /// </summary>
    public Func<ReadOnlySpan<byte>, int>? LineLimitFor { get; init; }
}
