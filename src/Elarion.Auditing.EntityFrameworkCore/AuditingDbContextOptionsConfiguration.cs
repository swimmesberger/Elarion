using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Auditing.EntityFrameworkCore;

/// <summary>
/// Attaches the <see cref="AuditSaveChangesInterceptor"/> to every <typeparamref name="TContext"/> instance so
/// audited invocations capture changes without the host calling <c>AddInterceptors</c> by hand. It resolves only
/// the audit interceptor (not <c>GetServices&lt;IInterceptor&gt;()</c>), so it composes cleanly with other
/// features that attach their own interceptors the same way (e.g. the settings change dispatch).
/// </summary>
/// <remarks>
/// EF Core applies every registered <see cref="IDbContextOptionsConfiguration{TContext}"/> when it builds the
/// context options, passing the context's own (scoped) service provider — so the resolved interceptor shares
/// the invocation's scope (and therefore its audit scope). Stateless, so registered as a singleton.
/// </remarks>
internal sealed class AuditingDbContextOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext>
    where TContext : DbContext {
    public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(serviceProvider.GetRequiredService<AuditSaveChangesInterceptor>());
}
