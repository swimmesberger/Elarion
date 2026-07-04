using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Settings.EntityFrameworkCore;

/// <summary>
/// Attaches the <see cref="SettingsChangeDispatchTransactionInterceptor"/> to every <typeparamref name="TContext"/>
/// instance so transactional settings writes are commit-gated without the host calling <c>AddInterceptors</c> by
/// hand. It resolves only the settings interceptor (not <c>GetServices&lt;IInterceptor&gt;()</c>), so it composes
/// cleanly with any other feature that attaches its own interceptors to the same context the same way.
/// </summary>
/// <remarks>
/// EF Core applies every registered <see cref="IDbContextOptionsConfiguration{TContext}"/> when it builds the
/// context options, passing the context's own (scoped) service provider — so the resolved interceptor is bound to
/// the same scope as the <see cref="SettingsChangeDispatchScope"/> the notifier buffers into, and the
/// commit/rollback flush therefore sees the right buffer. Stateless, so it is registered as a singleton.
/// </remarks>
internal sealed class SettingsChangeDispatchOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext {
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(serviceProvider.GetRequiredService<SettingsChangeDispatchTransactionInterceptor>());
}
