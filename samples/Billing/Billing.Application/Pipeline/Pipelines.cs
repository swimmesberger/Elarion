using System;
using Billing.Application.Decorators;
using Elarion.Abstractions.Pipeline;

namespace Billing.Application.Pipeline;

/// <summary>The one decorator pipeline for the whole application. Decorators run outermost-first in
/// list order: logging wraps everything, and the transaction wraps only the handler — and only on
/// commands and integration-event handlers, because <see cref="TransactionDecorator{TRequest,TResponse}"/>
/// declares an <c>AppliesTo</c> predicate the generator evaluates at compile time. Validation is not
/// listed: the framework <see cref="ValidationDecorator{TRequest,TResponse}"/> is auto-attached outside
/// this list (ADR-0027) for any handler whose request carries DataAnnotations, so a bad request still
/// never opens a transaction.</summary>
[DecoratorList(
    typeof(LoggingDecorator<,>),
    typeof(Elarion.Abstractions.Pipeline.TransactionDecorator<,>))]
[AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class)]
public sealed class DefaultPipelineAttribute : Attribute;
