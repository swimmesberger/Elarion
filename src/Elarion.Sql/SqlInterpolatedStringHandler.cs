using System.Collections;
using System.Runtime.CompilerServices;

namespace Elarion.Sql;

/// <summary>
/// The interpolated string handler behind the <see cref="SqlStatement"/> constructor and the <c>DbConnection</c>
/// query extensions: the compiler routes literal text to <see cref="AppendLiteral"/> and every
/// interpolated hole to an <c>AppendFormatted</c> overload, so values become
/// <see cref="System.Data.Common.DbParameter"/>s at compile time and injection is structurally
/// impossible — there is no string concatenation to get wrong.
/// </summary>
[InterpolatedStringHandler]
public struct SqlInterpolatedStringHandler {
    private readonly List<SqlSegment> _segments;
    private readonly int _literalLength;
    private readonly int _formattedCount;

    public SqlInterpolatedStringHandler(int literalLength, int formattedCount) {
        _segments = new List<SqlSegment>(formattedCount * 2 + 1);
        _literalLength = literalLength;
        _formattedCount = formattedCount;
    }

    public void AppendLiteral(string value) => _segments.Add(SqlSegment.OfLiteral(value));

    /// <summary>
    /// Splices a composed fragment; its parameters are renumbered into this statement. A pure-literal
    /// fragment (from <see cref="SqlStatement.Verbatim"/> or a generated <c>Table</c>/<c>Select</c>) inlines as
    /// a literal segment, so it costs no more than raw text — this is the identifier-splice fast path.
    /// </summary>
    public void AppendFormatted(SqlStatement? fragment) => fragment?.AddAsSegmentTo(_segments);

    /// <summary>
    /// Splices an optional-filter accumulator: renders <c>WHERE (…) AND (…)</c>, or nothing when no
    /// predicates were added. Its predicates carry their own parameters, so the composed WHERE stays
    /// injection-safe.
    /// </summary>
    public void AppendFormatted(SqlWhere? where) => where?.ToSql().AddAsSegmentTo(_segments);

    /// <summary>Binds a value as a parameter; collections expand to a parameter list for <c>IN</c>.</summary>
    public void AppendFormatted<T>(T value) {
        switch (value) {
            case SqlStatement fragment:
                fragment.AddAsSegmentTo(_segments);
                break;
            case SqlWhere where:
                where.ToSql().AddAsSegmentTo(_segments);
                break;
            // string and byte[] are IEnumerable but always scalar values.
            case IEnumerable items and not (string or byte[]):
                _segments.Add(SqlSegment.OfExpansion(items));
                break;
            default:
                _segments.Add(SqlSegment.OfValue(value));
                break;
        }
    }

    /// <summary>
    /// <c>{expr:raw}</c> splices the value's text verbatim — for trusted identifiers such as the
    /// generated <c>TableName</c>/column constants, never for user input. Any other format is an error.
    /// </summary>
    public void AppendFormatted<T>(T value, string format) {
        if (format != "raw") {
            throw new FormatException(
                $"Unknown SQL interpolation format '{format}' — the only supported format is 'raw' (verbatim splice of trusted text).");
        }

        _segments.Add(SqlSegment.OfLiteral(value?.ToString() ?? ""));
    }

    // Captured state read by the SqlStatement constructor.
    internal readonly List<SqlSegment> Segments => _segments;

    // Capacity hints: literal text plus ~4 chars per "@pN" placeholder; one value per hole.
    internal readonly int TextCapacityHint => _literalLength + (_formattedCount * 4);

    internal readonly int ValueCountHint => _formattedCount;
}
