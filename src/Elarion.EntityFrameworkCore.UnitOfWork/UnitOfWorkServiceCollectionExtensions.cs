using Elarion.Abstractions.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.EntityFrameworkCore.UnitOfWork;

/// <summary>
/// Registers the EF Core <see cref="IUnitOfWork"/> over <typeparamref name="TDbContext"/>, replacing any
/// default (in-memory) unit of work so the framework transaction and idempotency decorators use a real
/// database transaction.
/// </summary>
public static class UnitOfWorkServiceCollectionExtensions {
    /// <summary>Registers <see cref="EfUnitOfWork{TDbContext}"/> as the scoped <see cref="IUnitOfWork"/>.</summary>
    public static IServiceCollection AddElarionUnitOfWork<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IUnitOfWork>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork<TDbContext>>();

        return services;
    }
}
