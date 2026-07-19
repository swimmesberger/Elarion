using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Messaging.InMemory;

/// <summary>
/// Attaches the integration-event dispatch interceptors to every <typeparamref name="TContext"/> instance, so the
/// in-memory integration tier is commit-gated without the host calling <c>AddInterceptors</c> by hand.
/// </summary>
/// <remarks>
/// EF Core applies every registered <see cref="IDbContextOptionsConfiguration{TContext}"/> when it builds the
/// context options, passing the context's own (scoped) service provider. Resolving the scoped
/// <see cref="IInterceptor"/> services from that provider gives the interceptors bound to the same scope as the
/// <c>EventDispatchScope</c> the integration bus buffers into — so the commit/rollback flush sees the right buffer.
/// Stateless, so it is registered as a singleton.
/// </remarks>
internal sealed class EventDispatchOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext {
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) {
        optionsBuilder.AddInterceptors(serviceProvider.GetServices<IInterceptor>());
    }
}
