using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;

namespace Elarion.Pipeline;

/// <summary>
/// Answers the response members marked <see cref="VisibleWithAttribute"/> as <see langword="null"/> for a caller who
/// does not hold their permission. The generator attaches it, just inside the authorization gate, to every handler
/// whose response reaches such a member, and supplies the reflection-free copy function it emits for the response
/// type.
/// </summary>
/// <remarks>
/// <para>
/// It sits outside caching, idempotency and the transaction, so a cached or replayed answer is stored whole and hidden
/// per caller. A caller holding every permission the response uses gets the handler's value unchanged; anyone else
/// gets a copy. A failure whose payload is the response type (a preview that changed carries the new outcome) is
/// hidden the same way.
/// </para>
/// <para>
/// The decision is <see cref="IPermissionChecker"/>'s. A host without one registered hides every marked member, so a
/// missing registration never discloses a member.
/// </para>
/// </remarks>
/// <typeparam name="TRequest">The request type.</typeparam>
/// <typeparam name="TValue">The success value of the handler's <see cref="Result{T}"/>.</typeparam>
public sealed class VisibilityDecorator<TRequest, TValue> : IHandler<TRequest, Result<TValue>> {
    private readonly IHandler<TRequest, Result<TValue>> _inner;
    private readonly IPermissionChecker _permissions;
    private readonly string[] _usedPermissions;
    private readonly Func<TValue, IPermissionChecker, TValue> _hide;

    /// <summary>Creates the decorator. Called by generated pipeline code.</summary>
    /// <param name="inner">The rest of the pipeline.</param>
    /// <param name="permissions">The permission decision, or <see langword="null"/> to hide every marked member.</param>
    /// <param name="usedPermissions">Every permission a <see cref="VisibleWithAttribute"/> in the response names.</param>
    /// <param name="hide">The generated function that copies a value with the members the caller may not see hidden.</param>
    public VisibilityDecorator(
        IHandler<TRequest, Result<TValue>> inner,
        IPermissionChecker? permissions,
        string[] usedPermissions,
        Func<TValue, IPermissionChecker, TValue> hide) {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(usedPermissions);
        ArgumentNullException.ThrowIfNull(hide);
        _inner = inner;
        _permissions = permissions ?? NoPermissions.Instance;
        _usedPermissions = usedPermissions;
        _hide = hide;
    }

    /// <inheritdoc />
    public async ValueTask<Result<TValue>> HandleAsync(TRequest request, CancellationToken ct) {
        var result = await _inner.HandleAsync(request, ct).ConfigureAwait(false);
        if (result.IsSuccess)
            return HoldsEveryUsedPermission() ? result : Result<TValue>.Success(_hide(result.Value, _permissions));

        if (result.Error.Data is TValue data && !HoldsEveryUsedPermission())
            return Result<TValue>.Failure(result.Error with { Data = _hide(data, _permissions) });

        return result;
    }

    private bool HoldsEveryUsedPermission() {
        foreach (var permission in _usedPermissions)
            if (!_permissions.Holds(permission))
                return false;

        return true;
    }
}

/// <summary>The decision of a host without an <see cref="IPermissionChecker"/>: nothing is held (fail closed).</summary>
internal sealed class NoPermissions : IPermissionChecker {
    public static readonly NoPermissions Instance = new();

    public bool Holds(string permission) {
        return false;
    }
}
