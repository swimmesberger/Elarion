namespace Elarion.Abstractions.Authorization;

/// <summary>
/// Evaluates <see cref="AuthorizationRequirements"/> for the current principal. Transport-neutral: an
/// implementation reads the principal from <see cref="Identity.ICurrentUser"/> (or an equivalent host
/// abstraction), never from an HTTP context.
/// </summary>
public interface IAuthorizer {
    /// <summary>
    /// Returns <see langword="null"/> when authorized; otherwise an <see cref="AppError"/> describing the
    /// first failed requirement — <see cref="AppError.Unauthorized(string)"/> when the principal is
    /// unauthenticated, <see cref="AppError.Forbidden(string)"/> when authenticated but lacking a requirement.
    /// </summary>
    /// <param name="requirements">The requirements to satisfy.</param>
    /// <param name="resource">The handler request, supplied to named policies as the resource.</param>
    /// <param name="ct">A cancellation token.</param>
    ValueTask<AppError?> AuthorizeAsync(AuthorizationRequirements requirements, object? resource, CancellationToken ct);
}
