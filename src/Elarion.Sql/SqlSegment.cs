namespace Elarion.Sql;

internal enum SqlSegmentKind : byte {
    /// <summary>Literal SQL text, passed through untouched.</summary>
    Literal,

    /// <summary>A single interpolated value, bound as one <c>@pN</c> parameter.</summary>
    Value,

    /// <summary>
    /// An interpolated collection, expanded to a parenthesized parameter list for <c>IN</c>
    /// (an empty collection fails loud at build time — no spelling keeps both <c>IN</c> and
    /// <c>NOT IN</c> correct for the empty set).
    /// </summary>
    Expansion,

    /// <summary>A nested <see cref="SqlStatement"/> fragment, spliced with its parameters renumbered.</summary>
    Fragment,
}

/// <summary>One piece of an interpolated SQL statement, kept so fragments can compose losslessly.</summary>
internal readonly struct SqlSegment(SqlSegmentKind kind, string? literal, object? value) {
    internal SqlSegmentKind Kind { get; } = kind;
    internal string? Literal { get; } = literal;
    internal object? Value { get; } = value;

    internal static SqlSegment OfLiteral(string text) => new(SqlSegmentKind.Literal, text, null);
    internal static SqlSegment OfValue(object? value) => new(SqlSegmentKind.Value, null, value);
    internal static SqlSegment OfExpansion(object value) => new(SqlSegmentKind.Expansion, null, value);
    internal static SqlSegment OfFragment(SqlStatement fragment) => new(SqlSegmentKind.Fragment, null, fragment);
}
