namespace Elarion.Abstractions.Results;

/// <summary>
/// A type with exactly one value, used as the "no value" payload for <see cref="Result{T}"/>.
/// A handler that produces no meaningful response returns <see cref="Result{T}"/> of
/// <see cref="Unit"/> (commonly via the <see cref="IHandler{T}"/> convenience interface and
/// the non-generic <see cref="Result"/>).
/// </summary>
/// <remarks>
/// <para>
/// This type lives in the dedicated <c>Elarion.Abstractions.Results</c> namespace rather than the
/// root <c>Elarion.Abstractions</c> namespace that handlers import for <c>IHandler</c>/<c>Result</c>/
/// <c>AppError</c>. <c>Unit</c> is a common domain noun (units of measure, org units, rental units),
/// so keeping it out of the always-imported namespace avoids <c>CS0104</c> ambiguity with a domain
/// <c>Unit</c> type. Import <c>using Elarion.Abstractions.Results;</c> only where you reference
/// <c>Unit</c> directly; when a domain <c>Unit</c> is also in scope, alias it
/// (e.g. <c>using ResultUnit = Elarion.Abstractions.Results.Unit;</c>).
/// </para>
/// </remarks>
public readonly struct Unit : IEquatable<Unit> {
    /// <summary>The single <see cref="Unit"/> value.</summary>
    public static Unit Value => default;

    /// <inheritdoc />
    public bool Equals(Unit other) {
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is Unit;
    }

    /// <inheritdoc />
    public override int GetHashCode() {
        return 0;
    }

    /// <summary>All <see cref="Unit"/> values are equal.</summary>
    public static bool operator ==(Unit left, Unit right) {
        return true;
    }

    /// <summary>All <see cref="Unit"/> values are equal.</summary>
    public static bool operator !=(Unit left, Unit right) {
        return false;
    }

    /// <inheritdoc />
    public override string ToString() {
        return "()";
    }
}
