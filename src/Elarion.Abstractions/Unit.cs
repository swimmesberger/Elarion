namespace Elarion.Abstractions;

/// <summary>
/// A type with exactly one value, used as the "no value" payload for <see cref="Result{T}"/>.
/// A handler that produces no meaningful response returns <see cref="Result{T}"/> of
/// <see cref="Unit"/> (commonly via the <see cref="IHandler{T}"/> convenience interface and
/// the non-generic <see cref="Result"/>).
/// </summary>
public readonly struct Unit : IEquatable<Unit> {
    /// <summary>The single <see cref="Unit"/> value.</summary>
    public static Unit Value => default;

    /// <inheritdoc />
    public bool Equals(Unit other) => true;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Unit;

    /// <inheritdoc />
    public override int GetHashCode() => 0;

    /// <summary>All <see cref="Unit"/> values are equal.</summary>
    public static bool operator ==(Unit left, Unit right) => true;

    /// <summary>All <see cref="Unit"/> values are equal.</summary>
    public static bool operator !=(Unit left, Unit right) => false;

    /// <inheritdoc />
    public override string ToString() => "()";
}
