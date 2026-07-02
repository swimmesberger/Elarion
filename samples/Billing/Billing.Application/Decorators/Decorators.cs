using Elarion.Abstractions;
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

// The transaction/unit-of-work decorator is framework-owned
// (Elarion.Abstractions.Pipeline.TransactionDecorator, over IUnitOfWork) so features like idempotency compose
// on the same boundary. The sample references it directly in its [DecoratorList] and registers the EF unit of
// work with AddElarionUnitOfWork<BillingDbContext>().
//
// Validation is framework-owned too (ADR-0027): wire-shape rules are DataAnnotations on the request DTOs, and
// the handler generator auto-attaches Elarion.Abstractions.Pipeline.ValidationDecorator (over the
// IRequestValidator seam) for any handler whose request carries them — it must never appear in a
// [DecoratorList]. The host enables enforcement with AddElarionValidation(); business rules (cross-field,
// async/database-backed) live in the handlers themselves.
