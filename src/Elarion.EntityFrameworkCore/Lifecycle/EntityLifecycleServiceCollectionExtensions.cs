using Elarion.EntityFrameworkCore.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.EntityFrameworkCore.Lifecycle;

/// <summary>Registration of entity lifecycle events for an application context.</summary>
public static class EntityLifecycleServiceCollectionExtensions {
    /// <summary>
    /// Publishes <see cref="Elarion.Abstractions.Messaging.EntitiesDeleted{TEntity}"/> for the
    /// <see cref="Elarion.Abstractions.Messaging.PublishLifecycleEventsAttribute"/> entities every
    /// <typeparamref name="TDbContext"/> save deletes, and refuses an <c>ExecuteDelete</c> that would bypass it.
    /// Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interceptor is attached through an <see cref="IDbContextOptionsConfiguration{TContext}"/>, so the host calls no
    /// <c>AddInterceptors</c>; it resolves the scoped domain event bus from the context's application service provider,
    /// which requires <c>AddDbContext</c> with scoped options (the default). A context from <c>AddDbContextFactory</c>, a
    /// pool, or singleton options is built outside the scope its consumers use, and is refused when it saves a deletion.
    /// </para>
    /// <para>
    /// <b>Order.</b> EF Core runs <c>SaveChanges</c> interceptors in the order they were added, and configurations apply in
    /// registration order. The consumers add and remove entities inside this interceptor's <c>SavingChanges</c>, so
    /// register it before every interceptor that must see their changes: tenant scoping
    /// (<c>AddElarionTenantScopingEntityFrameworkCore</c>), which stamps the rows they add, and auditing
    /// (<c>AddElarionAuditingEntityFrameworkCore</c>), which records the rows they remove. Building a context with either
    /// of those interceptors already attached throws. Interceptors added in <c>AddDbContext</c>'s own options callback
    /// run at that callback's registration position, usually first — right for a soft-delete interceptor, which must
    /// turn a deletion into an update before this one sees it.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddElarionEntityLifecycleEvents<TDbContext>(this IServiceCollection services)
        where TDbContext : DbContext {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IDbContextOptionsConfiguration<TDbContext>, EntityLifecycleDbContextOptionsConfiguration<TDbContext>>());
        return services;
    }

    private sealed class EntityLifecycleDbContextOptionsConfiguration<TContext> : IDbContextOptionsConfiguration<TContext>
        where TContext : DbContext {
        // Elarion's save interceptors that must see what EntitiesDeleted consumers change. Auditing is named: its
        // package is not a reference of this one.
        private static readonly string[] MustRunAfter = [
            typeof(TenantScopeSaveChangesInterceptor).FullName!,
            "Elarion.Auditing.EntityFrameworkCore.AuditSaveChangesInterceptor"
        ];

        public void Configure(IServiceProvider serviceProvider, DbContextOptionsBuilder optionsBuilder) {
            // A wrong order fails silently (rows a consumer adds miss their tenant, rows it removes miss the audit), so
            // it fails here, when the first context is built.
            var earlier = optionsBuilder.Options.FindExtension<CoreOptionsExtension>()?.Interceptors ?? [];
            if (earlier.FirstOrDefault(interceptor => MustRunAfter.Contains(interceptor.GetType().FullName)) is { } first)
                throw new InvalidOperationException(
                    $"AddElarionEntityLifecycleEvents<{typeof(TContext).Name}>() is registered after the service that adds " +
                    $"{first.GetType().Name}, which must see the changes EntitiesDeleted consumers make. Call it before " +
                    "AddElarionTenantScopingEntityFrameworkCore and AddElarionAuditingEntityFrameworkCore.");

            optionsBuilder.AddInterceptors(EntityLifecycleInterceptor.Instance);
        }
    }
}
