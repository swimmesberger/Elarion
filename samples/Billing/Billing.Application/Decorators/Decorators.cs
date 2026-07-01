using Elarion.Abstractions;
using FluentValidation;
using Microsoft.Extensions.Logging;

namespace Billing.Application.Decorators;

public sealed class LoggingDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    ILogger<LoggingDecorator<TRequest, TResponse>> logger
) : IHandler<TRequest, TResponse> {
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        logger.LogInformation("Handling {Request}", typeof(TRequest).DeclaringType?.Name ?? typeof(TRequest).Name);
        return await inner.HandleAsync(request, ct);
    }
}

public sealed class ValidationDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    IEnumerable<IValidator<TRequest>> validators
) : IHandler<TRequest, TResponse>
    // Constraining TResponse to the framework's static-abstract failure factory lets the decorator build a
    // failed result without reflection; the generator evaluates this constraint at pipeline-build time, so the
    // decorator only attaches to handlers whose response is a Result<T>/Result.
    where TResponse : IResultFailureFactory<TResponse> {
    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        var failures = new List<string>();
        foreach (var validator in validators) {
            var result = await validator.ValidateAsync(request, ct);
            failures.AddRange(result.Errors.Select(e => e.ErrorMessage));
        }

        if (failures.Count == 0) {
            return await inner.HandleAsync(request, ct);
        }

        return TResponse.Failure(
            AppError.Validation(string.Join("; ", failures), failures));
    }
}

// The transaction/unit-of-work decorator is now framework-owned
// (Elarion.Abstractions.Pipeline.TransactionDecorator, over IUnitOfWork) so features like idempotency compose
// on the same boundary. The sample references it directly in its [DecoratorList] and registers the EF unit of
// work with AddElarionUnitOfWork<BillingDbContext>().
