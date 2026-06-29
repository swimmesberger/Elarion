using System;
using Elarion.Abstractions;
using Elarion.Abstractions.Messaging;
using Elarion.Abstractions.Pipeline;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
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

public sealed class TransactionDecorator<TRequest, TResponse>(
    IHandler<TRequest, TResponse> inner,
    DbContext db
) : IHandler<TRequest, TResponse> {
    // Attach only where a new unit of work is needed — commands and integration-event handlers. The generator
    // evaluates this once per handler at pipeline-build time, so queries and domain-event handlers never get it.
    public static bool AppliesTo(HandlerMetadata handler) =>
        handler.RequestType.IsAssignableTo(typeof(ICommand)) ||
        handler.RequestType.IsAssignableTo(typeof(IIntegrationEvent));

    public async ValueTask<TResponse> HandleAsync(TRequest request, CancellationToken ct) {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var response = await inner.HandleAsync(request, ct);

        if (response is IResultLike { IsSuccess: true }) {
            await transaction.CommitAsync(ct);
        } else {
            await transaction.RollbackAsync(ct);
        }

        return response;
    }
}
