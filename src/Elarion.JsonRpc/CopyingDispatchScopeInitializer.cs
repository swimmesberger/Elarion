using Microsoft.Extensions.DependencyInjection;

namespace Elarion.JsonRpc;

/// <summary>
/// A generic <see cref="IDispatchScopeInitializer"/> that copies the originating request scope's
/// <typeparamref name="T"/> into each per-call dispatch scope via <see cref="IScopeCopyable{T}.CopyFrom"/> —
/// so a scoped service built once per request is reused across the request's dispatch child scopes instead of
/// rebuilt per call. Register it with
/// <see cref="DispatchScopeServiceCollectionExtensions.AddDispatchScopeInherited{T}"/>.
/// </summary>
/// <remarks>
/// This only applies where a request scope exists to inherit from (JSON-RPC, HTTP batch). For MCP — whose
/// per-call scope is rooted at the session / application root with no request scope — <c>inheritFrom</c> is
/// <see langword="null"/> and this is a no-op; seed such transports from the
/// <see cref="DispatchScopeContext"/> in a dedicated initializer instead.
/// </remarks>
/// <typeparam name="T">The scoped service to inherit; it must be registered and implement <see cref="IScopeCopyable{T}"/>.</typeparam>
public sealed class CopyingDispatchScopeInitializer<T> : IDispatchScopeInitializer
    where T : class, IScopeCopyable<T> {
    /// <inheritdoc />
    public void Initialize(IServiceProvider callScope, IServiceProvider? inheritFrom, DispatchScopeContext context) {
        if (inheritFrom?.GetService<T>() is { } source) {
            callScope.GetRequiredService<T>().CopyFrom(source);
        }
    }
}
