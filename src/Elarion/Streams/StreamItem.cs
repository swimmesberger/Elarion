namespace Elarion.Streams;

/// <summary>
/// One element of a <see cref="StreamHub{T}"/> with its hub-assigned position. Sequences are contiguous
/// per hub (starting at 1), so a consumer detects loss — an overflow drop or a resume gap that outran the
/// replay ring — as a jump in <see cref="Sequence"/> instead of a silent hole.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
/// <param name="Sequence">The hub-assigned, strictly increasing position of this element.</param>
/// <param name="Value">The element.</param>
public readonly record struct StreamItem<T>(long Sequence, T Value);
