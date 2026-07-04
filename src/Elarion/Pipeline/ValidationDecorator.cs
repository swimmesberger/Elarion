using Elarion.Abstractions.Validation;
using Elarion.Abstractions;

namespace Elarion.Pipeline;

/// <summary>
/// Validates the request through the <see cref="IRequestValidator"/> seam before it reaches the handler: a
/// request violating its declarative shape constraints fails with <see cref="ErrorKind.Validation"/> — carrying
/// the field-keyed violations — without touching caching, the pipeline, or the transaction.
/// </summary>
/// <remarks>
/// Framework-owned (ADR-0027). The handler generator attaches it just inside the feature gate
/// (tracing → authorization → feature gate → <b>validation</b> → pipeline → handler) for any handler whose
/// request type carries validation metadata, so an unvalidatable request costs nothing. Constraining
/// <typeparamref name="TResponse"/> to the static-abstract failure factory lets the decorator build a failed
/// result without reflection; the generator evaluates the constraint at pipeline-build time.
/// </remarks>
public sealed class ValidationDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IRequestValidator validator
) : IHandler<TRequest, TResponse>
    where TResponse : IResultFailureFactory<TResponse> {
    /// <inheritdoc />
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var errors = await validator.ValidateAsync(typeof(TRequest), request!, ct).ConfigureAwait(false);
        if (errors is null) {
            return await inner.HandleAsync(request, ct).ConfigureAwait(false);
        }

        var messages = ValidationErrorData.Flatten(errors.FieldErrors);
        return TResponse.Failure(AppError.Validation(string.Join("; ", messages), errors.FieldErrors));
    }
}
