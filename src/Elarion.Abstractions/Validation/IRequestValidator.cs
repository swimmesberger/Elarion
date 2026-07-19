namespace Elarion.Abstractions.Validation;

/// <summary>
/// The request-validation seam: validates a request's declarative shape constraints (ADR-0027) and reports
/// the violations keyed by wire-named field path. The framework's
/// <see cref="Pipeline.ValidationDecorator{TRequest, TResponse}"/> calls this before the request reaches
/// caching, the pipeline, or the transaction.
/// </summary>
/// <remarks>
/// Implementation-neutral by design (the same seam/impl split as caching and resilience, ADR-0017): the
/// default implementation over <c>Microsoft.Extensions.Validation</c> lives in the opt-in
/// <c>Elarion.Validation</c> package, and a host can replace it wholesale without touching any handler.
/// </remarks>
public interface IRequestValidator {
    /// <summary>
    /// Validates <paramref name="request"/> against the validation metadata registered for
    /// <paramref name="requestType"/>.
    /// </summary>
    /// <param name="requestType">The request's declared type (the handler's <c>TRequest</c>).</param>
    /// <param name="request">The request instance to validate.</param>
    /// <param name="cancellationToken">Cancels the validation.</param>
    /// <returns>
    /// <see langword="null"/> when the request is valid — including when <paramref name="requestType"/> has no
    /// validation metadata at all — otherwise the field-keyed violations.
    /// </returns>
    ValueTask<RequestValidationErrors?> ValidateAsync(Type requestType, object request,
        CancellationToken cancellationToken);
}
