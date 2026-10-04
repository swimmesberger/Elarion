namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Evaluates <see cref="AuthorizationRequirements"/> for the current principal. Transport-neutral: an
/// implementation reads the principal from <see cref="Identity.ICurrentUser"/> (or an equivalent host
/// abstraction), never from an HTTP context.
/// </summary>
public interface IAuthorizer {
    /// <summary>
    /// Returns <see langword="null"/> when authorized; otherwise an <see cref="AppError"/> describing the
    /// first failed requirement — <see cref="AppError.Unauthorized(string, string, object)"/> when the principal is
    /// unauthenticated, <see cref="AppError.Forbidden(string, string, object)"/> when authenticated but lacking a requirement.
    /// </summary>
    /// <param name="requirements">The requirements to satisfy.</param>
    /// <param name="resource">The handler request, supplied to named policies as the resource.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask<AppError?> AuthorizeAsync(AuthorizationRequirements requirements, object? resource, CancellationToken ct);

    /// <summary>
    /// Evaluates only the part of <paramref name="requirements"/> that does not depend on the request payload:
    /// the anonymous opt-out, the authentication gate, and the declared permissions, roles and claims. Named
    /// policies, global rules and resource requirements receive the request and so are not evaluated here. Used by
    /// transports that fail to bind a payload to decide between an authentication/authorization error and a payload
    /// error (see <c>IHandlerGate</c>); it must agree with <see cref="AuthorizeAsync"/> — a request this method
    /// denies, <see cref="AuthorizeAsync"/> denies too.
    /// </summary>
    /// <param name="requirements">The requirements to satisfy; <see cref="AuthorizationRequirements.Resources"/> is ignored.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask<AppError?> AuthorizeGateAsync(AuthorizationRequirements requirements, CancellationToken ct);
}
