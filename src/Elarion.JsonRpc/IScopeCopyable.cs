namespace Elarion.JsonRpc;

/// <summary>
/// A scoped service whose per-request state can be copied into the dispatcher's per-call child scope instead
/// of being rebuilt. Implement this on a scoped service and register it with
/// <see cref="DispatchScopeServiceCollectionExtensions.AddDispatchScopeInherited{T}"/> so
/// <see cref="CopyingDispatchScopeInitializer{T}"/> copies the originating request-scope instance into each
/// JSON-RPC / HTTP-batch call scope — avoiding the cost of reconstructing it per call.
/// </summary>
/// <typeparam name="T">The implementing type.</typeparam>
public interface IScopeCopyable<in T> {
    /// <summary>Copies the request-scoped state of <paramref name="source"/> into this instance.</summary>
    void CopyFrom(T source);
}
