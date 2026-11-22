using System.Collections.Immutable;
using System.ComponentModel;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;

namespace Elarion.Pipeline;

/// <summary>
/// The collection and wrapper copies generated <see cref="VisibilityDecorator{TRequest, TValue}"/> code uses to hide
/// <see cref="VisibleWithAttribute"/> members nested in a response: each applies the element's generated copy function
/// and returns a new instance of the declared shape. Not meant to be called by hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ResponseRedaction {
    /// <summary>Copies an array, element by element.</summary>
    public static T[]? Array<T>(T[]? source, Func<T, IPermissionChecker, T> hide, IPermissionChecker permissions) {
        if (source is null) return null;

        var copy = new T[source.Length];
        for (var i = 0; i < source.Length; i++)
            copy[i] = hide(source[i], permissions);

        return copy;
    }

    /// <summary>Copies a <see cref="List{T}"/>, element by element.</summary>
    public static List<T>? List<T>(List<T>? source, Func<T, IPermissionChecker, T> hide, IPermissionChecker permissions) {
        if (source is null) return null;

        var copy = new List<T>(source.Count);
        foreach (var item in source)
            copy.Add(hide(item, permissions));

        return copy;
    }

    /// <summary>
    /// Copies any sequence into an array, which satisfies every read-only and list interface a member may be declared
    /// as (<see cref="IEnumerable{T}"/>, <see cref="IReadOnlyList{T}"/>, <see cref="IList{T}"/>, …).
    /// </summary>
    public static T[]? Sequence<T>(
        IEnumerable<T>? source, Func<T, IPermissionChecker, T> hide, IPermissionChecker permissions) {
        if (source is null) return null;

        var copy = source is IReadOnlyCollection<T> sized ? new List<T>(sized.Count) : [];
        foreach (var item in source)
            copy.Add(hide(item, permissions));

        return [.. copy];
    }

    /// <summary>Copies an <see cref="ImmutableArray{T}"/>, element by element.</summary>
    public static ImmutableArray<T> ImmutableArray<T>(
        ImmutableArray<T> source, Func<T, IPermissionChecker, T> hide, IPermissionChecker permissions) {
        if (source.IsDefault) return source;

        var copy = System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(source.Length);
        foreach (var item in source)
            copy.Add(hide(item, permissions));

        return copy.MoveToImmutable();
    }

    /// <summary>Copies a nullable struct's value.</summary>
    public static T? Nullable<T>(T? source, Func<T, IPermissionChecker, T> hide, IPermissionChecker permissions)
        where T : struct {
        return source is { } value ? hide(value, permissions) : null;
    }

    /// <summary>Copies a created-resource response, keeping its <see cref="ElarionCreated{T}.Location"/>.</summary>
    public static ElarionCreated<T>? Created<T>(
        ElarionCreated<T>? source, Func<T, IPermissionChecker, T> hide, IPermissionChecker permissions) {
        if (source is null) return null;

        return new ElarionCreated<T>(hide(source.Value, permissions)) { Location = source.Location };
    }
}
