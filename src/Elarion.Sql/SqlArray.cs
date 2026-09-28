namespace Elarion.Sql;

/// <summary>
/// A collection bound as <b>one</b> array-valued parameter instead of an expanded <c>IN</c> list — the
/// explicit opt-in for SQL that expects an array value, such as PostgreSQL's <c>= ANY(…)</c>,
/// <c>&lt;&gt; ALL(…)</c>, and the array operators <c>@&gt;</c>, <c>&lt;@</c>, and <c>&amp;&amp;</c>.
/// </summary>
/// <remarks>
/// <para>
/// A plain collection interpolated into a statement expands to <c>(@p0, @p1, …)</c> for <c>IN</c>; that is
/// the wrong shape wherever SQL expects a single array value, and the mistake would only surface when the
/// database rejects the statement (the <c>ELSQL012</c> analyzer reports it at build time). Wrapping the
/// collection states the array intent explicitly: it renders one <c>@pN</c> placeholder whose value is a
/// typed <c>T[]</c>, so the statement has one parameter — and one cached plan — whatever the length.
/// </para>
/// <para>
/// An empty array is valid (unlike an empty <c>IN</c> list): <c>= ANY(@p0)</c> matches nothing and
/// <c>&lt;&gt; ALL(@p0)</c> matches everything, both correct for the empty set.
/// </para>
/// <para>
/// Array parameters need a provider with array support. PostgreSQL (Npgsql) binds the value as the matching
/// PostgreSQL array type (<c>long[]</c> → <c>bigint[]</c>, <c>Guid[]</c> → <c>uuid[]</c>, …). Any other
/// provider fails with <see cref="NotSupportedException"/> when the statement is bound to its command,
/// before anything executes — interpolate the collection directly for an <c>IN</c> list there instead.
/// </para>
/// <para>
/// A value type, so wrapping a collection allocates nothing beyond the materialized array: an existing
/// <c>T[]</c> is bound by reference (do not mutate it until the statement has executed), and any other
/// sequence is copied into a new array once, when the wrapper is created.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// long[] ids = [1, 2, 3];
/// var rows = await db.QueryAsync&lt;Order&gt;($"{Order.Select} WHERE id = ANY({SqlArray.Of(ids)})", ct);
/// // renders: … WHERE id = ANY(@p0)   with @p0 = bigint[] {1, 2, 3}
/// </code>
/// </example>
public readonly struct SqlArray {
    private SqlArray(Array values) {
        Values = values;
    }

    /// <summary>
    /// The array bound as the parameter value, or <see langword="null"/> for <c>default(SqlArray)</c>, which
    /// carries no values and fails when its statement is built.
    /// </summary>
    internal Array? Values { get; }

    /// <summary>Wraps <paramref name="values"/> so it binds as one array parameter; the array is not copied.</summary>
    /// <typeparam name="T">The element type; it determines the database array type.</typeparam>
    /// <param name="values">The array to bind. It is bound by reference, so leave it unmodified until the
    /// statement has executed.</param>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is <see cref="byte"/>: a <c>byte[]</c> is
    /// always a scalar binary value.</exception>
    public static SqlArray Of<T>(T[] values) {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfByteElements<T>(nameof(values));
        return new SqlArray(values);
    }

    /// <summary>Wraps <paramref name="values"/> so it binds as one array parameter.</summary>
    /// <typeparam name="T">The element type; it determines the database array type.</typeparam>
    /// <param name="values">The sequence to bind. A <c>T[]</c> is bound by reference; any other sequence is
    /// enumerated once, here, into a new array.</param>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> is <see cref="byte"/>: a <c>byte[]</c> is
    /// always a scalar binary value.</exception>
    public static SqlArray Of<T>(IEnumerable<T> values) {
        ArgumentNullException.ThrowIfNull(values);
        ThrowIfByteElements<T>(nameof(values));
        return new SqlArray(values as T[] ?? [.. values]);
    }

    // ADO.NET providers bind byte[] as a binary scalar (bytea, BLOB), never as an array of integers, so a
    // byte-element SqlArray would silently change meaning. Reject it rather than bind the wrong type.
    private static void ThrowIfByteElements<T>(string paramName) {
        if (typeof(T) == typeof(byte))
            throw new ArgumentException(
                "A byte[] always binds as a scalar binary value, not as an array of integers — interpolate it "
                + "directly for a binary parameter, or widen the elements (for example to short) for an array "
                + "parameter.", paramName);
    }
}
