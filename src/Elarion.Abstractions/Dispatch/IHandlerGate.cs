using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Abstractions.Dispatch;

/// <summary>
/// A request-independent admission check of one handler — the part of its authorization that does not need the
/// bound request: the principal is authenticated and holds the declared permissions, roles and claims. A name-routed
/// or HTTP transport consults it <b>only when it fails to bind the request payload</b>, so a caller who is not
/// admitted gets the authentication or authorization error instead of a payload error. That keeps the
/// conventional 401/403-before-400 precedence and never discloses parameter requirements to a caller who may not
/// call the operation.
/// </summary>
/// <remarks>
/// <para>
/// The gate is deliberately <i>not</i> an early exit in front of the handler pipeline. When the payload binds, the
/// full decorator pipeline runs unchanged — the authorization decorator, the outer audit decorator and the handler
/// span still observe and record the denial — and the pipeline is the single authority for payload-dependent rules
/// (global rules, named policies, resource requirements), which stay after binding by necessity. The gate only
/// decides which error a request that never reaches the pipeline reports.
/// </para>
/// <para>
/// The handler-registration generator registers a gate keyed by the request type for every handler that has an
/// authorization decorator. Transports reach it through <see cref="HandlerRoute.EvaluateGateAsync"/> or, for
/// typed callers, <see cref="HandlerGates.EvaluateAsync{TRequest}"/>.
/// </para>
/// </remarks>
public interface IHandlerGate {
    /// <summary>
    /// Returns <see langword="null"/> when the caller is admitted, otherwise the
    /// <see cref="AppError.Unauthorized(string, string, object)"/> (unauthenticated) or
    /// <see cref="AppError.Forbidden(string, string, object)"/> (lacks a declared requirement) the transport should
    /// answer with.
    /// </summary>
    /// <param name="scope">The call scope, so the principal and authorizer resolve as they would for the handler.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask<AppError?> EvaluateAsync(IServiceProvider scope, CancellationToken ct);
}

/// <summary>Typed access to the <see cref="IHandlerGate"/> registered for a request type.</summary>
public static class HandlerGates {
    /// <summary>
    /// Evaluates the gate registered for <typeparamref name="TRequest"/>; <see langword="null"/> (admitted) when the
    /// handler has none.
    /// </summary>
    public static ValueTask<AppError?> EvaluateAsync<TRequest>(IServiceProvider scope, CancellationToken ct) {
        return EvaluateAsync(scope, typeof(TRequest), ct);
    }

    /// <summary>
    /// Evaluates the gate registered for <paramref name="requestType"/>; <see langword="null"/> (admitted) when the
    /// handler has none.
    /// </summary>
    public static ValueTask<AppError?> EvaluateAsync(IServiceProvider scope, Type requestType, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(requestType);
        return scope.GetKeyedService<IHandlerGate>(requestType) is { } gate
            ? gate.EvaluateAsync(scope, ct)
            : ValueTask.FromResult<AppError?>(null);
    }
}
