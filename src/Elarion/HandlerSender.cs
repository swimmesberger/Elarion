using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion;

/// <summary>
/// The default <see cref="IHandlerSender"/>: resolves the decorated <see cref="IHandler{TRequest,TResponse}"/>
/// from the ambient scope and invokes it. Registered scoped, so the injected <see cref="IServiceProvider"/> is
/// the caller's scope — the send runs in the caller's transaction.
/// </summary>
internal sealed class HandlerSender(IServiceProvider serviceProvider) : IHandlerSender {
    public ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(TRequest request, CancellationToken ct = default)
        where TRequest : notnull {
        ArgumentNullException.ThrowIfNull(request);
        return serviceProvider.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, ct);
    }
}

/// <summary>DI registration for the typed in-process <see cref="IHandlerSender"/>.</summary>
public static class HandlerSenderServiceCollectionExtensions {
    /// <summary>
    /// Registers the typed in-process <see cref="IHandlerSender"/> (scoped) so a handler can dispatch a request
    /// to another handler <b>by type</b>, within the caller's scope/transaction — the mediator-style send and the
    /// replacement for the removed <c>IDomainEventBus.RequestAsync</c> (see ADR-0010).
    /// </summary>
    public static IServiceCollection AddElarionHandlerSender(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<IHandlerSender, HandlerSender>();
        return services;
    }
}
