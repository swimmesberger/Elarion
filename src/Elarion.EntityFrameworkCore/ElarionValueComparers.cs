using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Elarion.EntityFrameworkCore;

/// <summary>
/// Blessed <see cref="ValueComparer"/>s for the collection shapes an application stores through a value
/// converter (a JSON column, a delimited string, a provider array type).
/// </summary>
/// <remarks>
/// <para>
/// A converted collection property needs a comparer, and hand-rolling one is where the subtle bug lives: EF
/// snapshots the property to detect changes, and the equality and hashing halves must <em>agree</em>. Pairing an
/// order-<em>independent</em> equality (a set comparison) with an order-<em>dependent</em> hash — or the reverse —
/// breaks the <see cref="object.GetHashCode"/> contract, and EF then misses or invents changes depending on how
/// the values happened to be ordered.
/// </para>
/// <para>
/// These comparers are consistently <b>order-dependent</b>: equality is <c>SequenceEqual</c> and the hash
/// aggregates the elements in order, both through <see cref="EqualityComparer{T}.Default"/>. Reordering a
/// sequence is therefore a change, which is the right default for a stored list — the persisted order is part of
/// the value. The snapshot is a shallow copy, so mutating the tracked instance in place is still detected;
/// elements themselves must be immutable (or value types) for that to hold.
/// </para>
/// <example>
/// <code>
/// builder.Property(e => e.Tags)
///     .HasConversion(
///         tags => string.Join(',', tags),
///         value => value.Split(',', StringSplitOptions.RemoveEmptyEntries),
///         ElarionValueComparers.Sequence&lt;string&gt;());
/// </code>
/// </example>
/// </remarks>
public static class ElarionValueComparers {
    /// <summary>
    /// An order-dependent comparer for an array-valued converted property: <c>SequenceEqual</c> equality, an
    /// order-sensitive hash over the same elements, and a shallow-copy snapshot.
    /// </summary>
    /// <typeparam name="T">The element type. Compared with <see cref="EqualityComparer{T}.Default"/>.</typeparam>
    public static ValueComparer<T[]> Sequence<T>() {
        // Static-method calls (rather than inline lambda bodies) keep these expression trees legal while the
        // logic stays ordinary C# — EF compiles the expressions, so there is no interpretation cost either way.
        return new ValueComparer<T[]>(
            (left, right) => AreEqual(left, right),
            value => ComputeHashCode(value),
            value => Snapshot(value));
    }

    /// <summary>
    /// The <see cref="List{T}"/> counterpart of <see cref="Sequence{T}"/>, with identical semantics — EF binds a
    /// comparer to the exact CLR type of the property, so a <c>List&lt;T&gt;</c> property needs this one.
    /// </summary>
    /// <typeparam name="T">The element type. Compared with <see cref="EqualityComparer{T}.Default"/>.</typeparam>
    public static ValueComparer<List<T>> SequenceList<T>() {
        return new ValueComparer<List<T>>(
            (left, right) => AreEqual(left, right),
            value => ComputeHashCode(value),
            value => SnapshotList(value));
    }

    private static bool AreEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        var comparer = EqualityComparer<T>.Default;
        for (var i = 0; i < left.Count; i++)
            if (!comparer.Equals(left[i], right[i]))
                return false;

        return true;
    }

    /// <summary>
    /// Hashes the elements <em>in order</em>, so the hash agrees with <see cref="AreEqual{T}"/> — the one
    /// invariant a hand-rolled collection comparer usually breaks.
    /// </summary>
    private static int ComputeHashCode<T>(IReadOnlyList<T>? values) {
        if (values is null)
            return 0;

        var hash = new HashCode();
        for (var i = 0; i < values.Count; i++) hash.Add(values[i]);

        return hash.ToHashCode();
    }

    private static T[] Snapshot<T>(T[]? value) {
        return value is null ? [] : (T[])value.Clone();
    }

    private static List<T> SnapshotList<T>(List<T>? value) {
        return value is null ? [] : new List<T>(value);
    }
}
