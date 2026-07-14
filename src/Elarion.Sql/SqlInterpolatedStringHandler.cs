using System.Collections;
using System.Runtime.CompilerServices;

namespace Elarion.Sql;

/// <summary>
/// The interpolated string handler behind <see cref="SqlStatement.Of(SqlInterpolatedStringHandler)"/> and the
/// <c>DbConnection</c> query extensions: the compiler routes literal text to
/// <see cref="AppendLiteral"/> and every interpolated hole to an <c>AppendFormatted</c> overload, so
/// values become <see cref="System.Data.Common.DbParameter"/>s at compile time and injection is
/// structurally impossible — there is no string concatenation to get wrong.
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

    /// <summary>Splices a composed fragment; its parameters are renumbered into this statement.</summary>
    public void AppendFormatted(SqlStatement? fragment) {
        if (fragment is not null) {
            _segments.Add(SqlSegment.OfFragment(fragment));
        }
    }

    /// <summary>Binds a value as a parameter; collections expand to a parameter list for <c>IN</c>.</summary>
    public void AppendFormatted<T>(T value) {
        switch (value) {
            case SqlStatement fragment:
                _segments.Add(SqlSegment.OfFragment(fragment));
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

    // Capacity hints: literal text plus ~4 chars per "@pN" placeholder; one value per hole.
    internal readonly SqlStatement ToSql() =>
        new(_segments, _literalLength + (_formattedCount * 4), _formattedCount);
}
