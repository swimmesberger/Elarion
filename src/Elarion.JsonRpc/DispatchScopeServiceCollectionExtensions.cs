using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.JsonRpc;

/// <summary>
/// Registration helpers for the per-call dispatch-scope seeding rail.
/// </summary>
public static class DispatchScopeServiceCollectionExtensions {
    /// <summary>
    /// Registers <typeparamref name="T"/> to be copied from the originating request scope into each per-call
    /// dispatch scope, so a request-scoped service is reused across the request's JSON-RPC / HTTP-batch call
    /// scopes instead of rebuilt per call. <typeparamref name="T"/> must be registered as a scoped service and
    /// implement <see cref="IScopeCopyable{T}"/>.
    /// </summary>
    /// <typeparam name="T">The scoped service to inherit.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddDispatchScopeInherited<T>(this IServiceCollection services)
        where T : class, IScopeCopyable<T> {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IDispatchScopeInitializer, CopyingDispatchScopeInitializer<T>>());
        return services;
    }
}
