using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Elarion.Generators;

/// <summary>
/// An immutable array with structural (sequence) value equality, suitable as a field in
/// incremental-generator pipeline models.
/// </summary>
/// <remarks>
/// <see cref="ImmutableArray{T}"/> compares by the underlying array <em>reference</em>, so a record
/// that holds one silently defeats the incremental cache (two structurally identical models compare
/// unequal and the generator re-emits on every edit). This wrapper compares element-by-element with
/// <see cref="EqualityComparer{T}"/>, so the element type only needs default value equality — no
/// <c>IEquatable&lt;T&gt;</c> constraint, which keeps enum element types (e.g. parameter kinds) usable.
/// An uninitialized (<c>default</c>) value reads as empty so a never-assigned field cannot throw.
/// </remarks>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
{
    public static readonly EquatableArray<T> Empty = new(ImmutableArray<T>.Empty);

    private readonly ImmutableArray<T> _array;

    public EquatableArray(ImmutableArray<T> array) => _array = array;

    /// <summary>The underlying array, normalized so a <c>default</c> value reads as empty.</summary>
    public ImmutableArray<T> AsImmutableArray => _array.IsDefault ? ImmutableArray<T>.Empty : _array;

    public int Count => AsImmutableArray.Length;

    /// <summary>Alias for <see cref="Count"/>, matching <see cref="ImmutableArray{T}.Length"/> so this is a drop-in.</summary>
    public int Length => AsImmutableArray.Length;

    public bool IsEmpty => AsImmutableArray.IsEmpty;

    public T this[int index] => AsImmutableArray[index];

    public bool Equals(EquatableArray<T> other)
    {
        var left = AsImmutableArray;
        var right = other.AsImmutableArray;
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        // Order-sensitive FNV-1a combine over element hashes; empty -> the seed constant.
        var hash = 2166136261u;
        foreach (var item in AsImmutableArray)
        {
            hash ^= (uint)(item?.GetHashCode() ?? 0);
            hash *= 16777619u;
        }

        return unchecked((int)hash);
    }

    public ImmutableArray<T>.Enumerator GetEnumerator() => AsImmutableArray.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)AsImmutableArray).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable)AsImmutableArray).GetEnumerator();

    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array) => new(array);

    public static implicit operator ImmutableArray<T>(EquatableArray<T> array) => array.AsImmutableArray;
}

internal static class EquatableArrayExtensions
{
    public static EquatableArray<T> ToEquatableArray<T>(this ImmutableArray<T> array) => new(array);

    public static EquatableArray<T> ToEquatableArray<T>(this IEnumerable<T> source) => new(source.ToImmutableArray());
}
