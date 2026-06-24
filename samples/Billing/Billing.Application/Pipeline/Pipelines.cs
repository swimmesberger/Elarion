using System;
using Billing.Application.Decorators;
using Elarion.Abstractions.Pipeline;

namespace Billing.Application.Pipeline;

/// <summary>The one decorator pipeline for the whole application. Decorators run outermost-first in
/// list order: logging wraps everything, validation runs before the transaction so a bad request never
/// opens one, and the transaction wraps only the handler — and only on commands and integration-event
/// handlers, because <see cref="TransactionDecorator{TRequest,TResponse}"/> declares an
/// <c>AppliesTo</c> predicate the generator evaluates at compile time.</summary>
[DecoratorList(
    typeof(LoggingDecorator<,>),
    typeof(ValidationDecorator<,>),
    typeof(TransactionDecorator<,>))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
public sealed class DefaultPipelineAttribute : Attribute;
