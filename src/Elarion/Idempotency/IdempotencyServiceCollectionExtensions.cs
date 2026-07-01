using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Idempotency;
using Elarion.Abstractions.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Idempotency;

/// <summary>
/// Registers the transport-neutral idempotency building blocks with their in-memory defaults: the scoped key
/// accessor, its dispatch-scope initializer, the in-memory store, and a no-op unit of work. A durable host
/// swaps the store and unit of work by calling <c>AddElarionIdempotencyEntityFrameworkCore&lt;TDbContext&gt;</c>.
/// </summary>
public static class IdempotencyServiceCollectionExtensions {
    /// <summary>Adds the idempotency accessor/initializer and the in-memory store + no-op unit of work.</summary>
    public static IServiceCollection AddElarionIdempotency(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        services.TryAddScoped<ScopedIdempotencyKeyAccessor>();
        services.TryAddScoped<IIdempotencyKeyAccessor>(sp => sp.GetRequiredService<ScopedIdempotencyKeyAccessor>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDispatchScopeInitializer, IdempotencyKeyScopeInitializer>());

        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        services.TryAddScoped<IUnitOfWork, InMemoryUnitOfWork>();

        return services;
    }
}
