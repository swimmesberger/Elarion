using Elarion.Abstractions;
using Elarion.Abstractions.Validation;

namespace Elarion.Pipeline;

/// <summary>Validates a request before accepting its stream.</summary>
public sealed class StreamValidationDecorator<TRequest, TItem>(
    IStreamHandler<TRequest, TItem> inner,
    IRequestValidator validator
) : IStreamHandler<TRequest, TItem> {
    public async ValueTask<Result<IAsyncEnumerable<TItem>>> HandleAsync(TRequest request, CancellationToken ct) {
        var errors = await validator.ValidateAsync(typeof(TRequest), request!, ct).ConfigureAwait(false);
        if (errors is not null)
            return AppError.Validation(string.Join("; ", ValidationErrorData.Flatten(errors.FieldErrors)),
                errors.FieldErrors);

        return await inner.HandleAsync(request, ct).ConfigureAwait(false);
    }
}
