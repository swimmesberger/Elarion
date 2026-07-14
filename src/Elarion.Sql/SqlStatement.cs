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
/// just a value; parameters are renumbered on composition.</item>
/// <item><c>{expr:raw}</c> splices the value verbatim (for trusted identifiers such as the generated
/// <c>TableName</c>/column constants — never for user input).</item>
/// </list>
/// </remarks>
/// <example>
/// <code>
/// SqlStatement where = SqlStatement.Of($"WHERE status = {status} AND id IN {ids}");
/// SqlStatement query = SqlStatement.Of($"SELECT {OrderSqlMapper.Columns.All:raw} FROM {OrderSqlMapper.TableName:raw} {where}");
/// var orders = await connection.QueryAsync(OrderSqlMapper.Instance, query, ct);
/// </code>
/// </example>
public sealed class SqlStatement {
    private readonly List<SqlSegment> _segments;
    private readonly int _textCapacityHint;
    private readonly int _valueCountHint;
    private List<object?>? _parameterValues;
    private string? _text;

    internal SqlStatement(List<SqlSegment> segments, int textCapacityHint, int valueCountHint) {
        _segments = segments;
        _textCapacityHint = textCapacityHint;
        _valueCountHint = valueCountHint;
    }

    /// <summary>Builds a statement from an interpolated string; values become parameters.</summary>
    /// <remarks>
    /// There is deliberately no <c>Of(string)</c> overload: a constant-only interpolated string is a
    /// C# constant expression, and overload resolution would prefer the <see cref="string"/> conversion
    /// — silently splicing a <c>{CONST}</c> hole as text instead of binding a parameter. With only the
    /// handler overload, every <c>$"…"</c> parameterizes; plain text goes through <see cref="Raw"/>.
    /// </remarks>
    public static SqlStatement Of(SqlInterpolatedStringHandler sql) => sql.ToSql();

    /// <summary>
    /// Wraps trusted text verbatim — plain parameterless SQL, or a dynamic identifier spliced into an
    /// interpolation (<c>{SqlStatement.Raw(orderByColumn)}</c>). Never pass user input.
    /// </summary>
    public static SqlStatement Raw(string sql) => new([SqlSegment.OfLiteral(sql)], sql.Length, 0);

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

    private static readonly List<object?> NoValues = [];

    private void Materialize() {
        if (_text is not null) {
            return;
        }

        // Fast path: a single literal (Raw, or an interpolation with no holes) IS its text.
        if (_segments.Count == 1 && _segments[0].Kind == SqlSegmentKind.Literal) {
            _parameterValues = NoValues;
            Volatile.Write(ref _text, _segments[0].Literal!);
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
