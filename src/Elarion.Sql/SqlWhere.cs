namespace Elarion.Sql;

/// <summary>
/// The optional-filter idiom for hand-written SQL, without a query DSL (ADR-0058): accumulate
/// predicate fragments, then splice the accumulator into a statement. It renders <c>""</c> when empty
/// and <c>WHERE (p1) AND (p2) …</c> otherwise — so the <c>WHERE 1=1</c> hack disappears and the same
/// accumulator can drive a page query and its <c>count(*)</c> companion. It knows only the
/// <c>WHERE</c>/<c>AND</c> joiners a human types mechanically; it generates no predicate — <c>OR</c>,
/// grouping, and joins are hand-written SQL inside a predicate fragment.
/// </summary>
/// <remarks>
/// Each predicate is parenthesized, so a predicate containing <c>OR</c> keeps its intended grouping
/// under the <c>AND</c> join. Predicates carry their own parameters (interpolated values become
/// <c>@pN</c> parameters), so a composed WHERE is injection-safe by construction — the obvious thing
/// to type is the safe thing.
/// </remarks>
/// <example>
/// <code>
/// var where = new SqlWhere();
/// where.And($"device_id = {deviceId}");
/// if (metric is not null) where.And($"metric = {metric}");
/// var rows  = await db.QueryAsync&lt;Reading&gt;($"{Reading.Select} {where} ORDER BY recorded_at DESC", ct);
/// var total = await db.ExecuteScalarAsync&lt;long&gt;($"SELECT count(*) FROM {Reading.Table} {where}", ct);
/// </code>
/// </example>
public sealed class SqlWhere {
    private readonly List<SqlStatement> _predicates = [];

    /// <summary>Adds a predicate fragment; interpolated values become parameters.</summary>
    public void And(SqlInterpolatedStringHandler predicate) => _predicates.Add(new SqlStatement(predicate));

    /// <summary>Adds an already-composed predicate fragment.</summary>
    public void And(SqlStatement predicate) {
        ArgumentNullException.ThrowIfNull(predicate);
        _predicates.Add(predicate);
    }

    /// <summary>Whether no predicates have been added (the accumulator renders to <c>""</c>).</summary>
    public bool IsEmpty => _predicates.Count == 0;

    /// <summary>
    /// Renders the accumulated predicates as <c>WHERE (p1) AND (p2) …</c>, or <see cref="SqlStatement.Empty"/>
    /// when empty. Splicing a <see cref="SqlWhere"/> into an interpolation calls this for you.
    /// </summary>
    public SqlStatement ToSql() {
        if (_predicates.Count == 0) {
            return SqlStatement.Empty;
        }

        // WHERE ( <p0> ) AND ( <p1> ) …  — two literals per predicate plus the fragment segment.
        var segments = new List<SqlSegment>((_predicates.Count * 3) + 1);
        for (var i = 0; i < _predicates.Count; i++) {
            segments.Add(SqlSegment.OfLiteral(i == 0 ? "WHERE (" : " AND ("));
            _predicates[i].AddAsSegmentTo(segments);
            segments.Add(SqlSegment.OfLiteral(")"));
        }

        return new SqlStatement(segments, textCapacityHint: 16 * _predicates.Count, valueCountHint: _predicates.Count);
    }
}
