using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Text;

namespace Elarion.Sql;

/// <summary>
/// An immutable, composable SQL statement with typed, injection-safe parameters — the C#-native
/// equivalent of jOOQ's plain-SQL templating tier (ADR-0058). Interpolated values become
/// <c>@p0, @p1, …</c> parameters at compile time via <see cref="SqlInterpolatedStringHandler"/>;
/// literal text passes through untouched, so full SQL stays full SQL.
/// </summary>
/// <remarks>
/// Interpolation rules:
/// <list type="bullet">
/// <item>A scalar value binds as one parameter (never concatenated).</item>
/// <item>A collection (except <see cref="string"/> and <see cref="T:byte[]"/>) expands to a
/// parenthesized parameter list for <c>IN</c>; an empty collection throws at build time (no SQL
/// spelling keeps both <c>IN</c> and <c>NOT IN</c> correct for the empty set — guard the query).</item>
/// <item>A nested <see cref="SqlStatement"/> value splices as a fragment — a reusable <c>WHERE</c> piece is
/// just a value; parameters are renumbered on composition. A pure-literal fragment (from
/// <see cref="Verbatim"/> or a generated <c>Table</c>/<c>Select</c>) inlines with no recursion.</item>
/// <item>Trusted identifiers (table/column names, a validated sort column) splice via
/// <see cref="Verbatim"/> — <c>{SqlStatement.Verbatim(orderByColumn)}</c> — never a raw <see cref="string"/>.</item>
/// </list>
/// There is deliberately no <see cref="string"/> conversion: a plain string interpolated into a query
/// binds as a parameter, never as SQL text, so injection is structurally impossible.
/// </remarks>
/// <example>
/// <code>
/// SqlStatement where = new($"WHERE status = {status} AND id IN {ids}");
/// var orders = await connection.QueryAsync&lt;Order&gt;($"{Order.Select} {where}", ct);
/// </code>
/// </example>
public sealed class SqlStatement {
    private readonly List<SqlSegment> _segments;
    private readonly int _textCapacityHint;
    private readonly int _valueCountHint;
    private List<object?>? _parameterValues;
    private string? _text;

    /// <summary>Builds a statement from an interpolated string; interpolated values become parameters.</summary>
    /// <remarks>
    /// There is deliberately no <c>SqlStatement(string)</c> constructor: a plain string would bypass
    /// parameterization. Trusted verbatim text goes through <see cref="Verbatim"/>.
    /// </remarks>
    public SqlStatement(SqlInterpolatedStringHandler sql)
        : this(sql.Segments, sql.TextCapacityHint, sql.ValueCountHint) {
    }

    internal SqlStatement(List<SqlSegment> segments, int textCapacityHint, int valueCountHint) {
        _segments = segments;
        _textCapacityHint = textCapacityHint;
        _valueCountHint = valueCountHint;
    }

    /// <summary>
    /// Wraps trusted text verbatim — parameterless SQL, or a dynamic identifier spliced into an
    /// interpolation (<c>{SqlStatement.Verbatim(orderByColumn)}</c>). Never pass user input: verbatim text is
    /// spliced into the command exactly, so it is an injection vector if it carries untrusted data.
    /// </summary>
    public static SqlStatement Verbatim(string trustedSql) => new([SqlSegment.OfLiteral(trustedSql)], trustedSql.Length, 0);

    /// <summary>The empty statement — renders to <c>""</c> with no parameters. Composes as a no-op.</summary>
    public static readonly SqlStatement Empty = new([], 0, 0);

    /// <summary>Concatenates two statements; the right side's parameters are renumbered after the left's.</summary>
    public static SqlStatement operator +(SqlStatement left, SqlStatement right) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var segments = new List<SqlSegment>(2);
        left.AddAsSegmentTo(segments);
        right.AddAsSegmentTo(segments);
        return new SqlStatement(segments, left._textCapacityHint + right._textCapacityHint,
            left._valueCountHint + right._valueCountHint);
    }

    /// <summary>The SQL text with <c>@p0, @p1, …</c> parameter placeholders.</summary>
    public string Text {
        get {
            Materialize();
            return _text!;
        }
    }

    /// <summary>The parameter values, positionally matching the <c>@pN</c> placeholders.</summary>
    public IReadOnlyList<object?> ParameterValues {
        get {
            Materialize();
            return _parameterValues!;
        }
    }

    /// <summary>Sets <see cref="Text"/> as the command text and adds one parameter per value.</summary>
    public void ApplyTo(DbCommand command) {
        Materialize();
        command.CommandText = _text!;
        var values = _parameterValues!;
        for (var i = 0; i < values.Count; i++) {
            var parameter = command.CreateParameter();
            parameter.ParameterName = ParameterName(i);
            parameter.Value = values[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }
    }

    /// <summary>Creates a command on <paramref name="connection"/> with this statement applied.</summary>
    public DbCommand CreateCommand(DbConnection connection) {
        var command = connection.CreateCommand();
        ApplyTo(command);
        return command;
    }

    public override string ToString() => Text;

    // A pure-literal statement (Verbatim, a generated Table/Select, or an interpolation with no holes) —
    // when spliced as a fragment it inlines as a literal segment, so it costs no more than raw text.
    internal bool TryGetLiteral(out string literal) {
        if (_segments.Count == 1 && _segments[0].Kind == SqlSegmentKind.Literal) {
            literal = _segments[0].Literal!;
            return true;
        }

        if (_segments.Count == 0) {
            literal = "";
            return true;
        }

        literal = "";
        return false;
    }

    // Appends this statement to a segment list, inlining a pure literal to avoid a fragment walk.
    internal void AddAsSegmentTo(List<SqlSegment> segments) {
        if (TryGetLiteral(out var literal)) {
            segments.Add(SqlSegment.OfLiteral(literal));
        }
        else {
            segments.Add(SqlSegment.OfFragment(this));
        }
    }

    private static readonly List<object?> NoValues = [];

    private void Materialize() {
        if (_text is not null) {
            return;
        }

        // Fast path: a pure literal (Verbatim, or an interpolation with no holes) IS its text.
        if (TryGetLiteral(out var literal)) {
            _parameterValues = NoValues;
            Volatile.Write(ref _text, literal);
            return;
        }

        var builder = new StringBuilder(_textCapacityHint);
        var values = new List<object?>(_valueCountHint);
        AppendTo(builder, values);
        _parameterValues = values;
        // Written last: readers gate on _text, so both fields are visible once it is.
        Volatile.Write(ref _text, builder.ToString());
    }

    internal void AppendTo(StringBuilder builder, List<object?> values) {
        foreach (var segment in _segments) {
            switch (segment.Kind) {
                case SqlSegmentKind.Literal:
                    builder.Append(segment.Literal);
                    break;
                case SqlSegmentKind.Value:
                    AppendParameter(builder, values, segment.Value);
                    break;
                case SqlSegmentKind.Expansion:
                    AppendExpansion(builder, values, (IEnumerable)segment.Value!);
                    break;
                case SqlSegmentKind.Fragment:
                    ((SqlStatement)segment.Value!).AppendTo(builder, values);
                    break;
            }
        }
    }

    private static void AppendParameter(StringBuilder builder, List<object?> values, object? value) {
        builder.Append('@').Append(ParameterName(values.Count));
        values.Add(value);
    }

    private static void AppendExpansion(StringBuilder builder, List<object?> values, IEnumerable items) {
        var first = true;
        foreach (var item in items) {
            builder.Append(first ? "(" : ", ");
            first = false;
            AppendParameter(builder, values, item);
        }

        if (first) {
            // No expansion is both type-correct and semantics-preserving for an empty set: "(NULL)"
            // silently flips NOT IN to match nothing, and an untyped never-matching subquery fails
            // PostgreSQL's type inference. Fail loud instead of returning silently wrong rows.
            throw new InvalidOperationException(
                "An empty collection cannot be expanded into a SQL IN list — guard the query before "
                + "building it (an empty IN matches nothing; an empty NOT IN matches everything).");
        }

        builder.Append(')');
    }

    // "p0".."p31" precomputed: parameter names are on every execution's hot path.
    private static readonly string[] CachedNames = [.. Enumerable.Range(0, 32).Select(i => "p" + i)];

    internal static string ParameterName(int index) =>
        index < CachedNames.Length ? CachedNames[index] : "p" + index.ToString(CultureInfo.InvariantCulture);
}
