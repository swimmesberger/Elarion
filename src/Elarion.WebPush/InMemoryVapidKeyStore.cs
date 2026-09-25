namespace Elarion.WebPush;

/// <summary>
/// The process-local <see cref="IVapidKeyStore"/> default. The key pair is lost on restart, which
/// invalidates every browser subscription — acceptable only next to the equally volatile
/// <see cref="InMemoryPushSubscriptionStore"/>, for tests and single-node development. Production uses
/// <c>Elarion.WebPush.EntityFrameworkCore</c> or configured keys.
/// </summary>
public sealed class InMemoryVapidKeyStore : IVapidKeyStore {
    private VapidKeys? _keys;

    /// <inheritdoc />
    public ValueTask<VapidKeys?> GetAsync(CancellationToken cancellationToken = default) {
        return ValueTask.FromResult(Volatile.Read(ref _keys));
    }

    /// <inheritdoc />
    public ValueTask<VapidKeys> GetOrAddAsync(VapidKeys candidate, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(candidate);
        return ValueTask.FromResult(Interlocked.CompareExchange(ref _keys, candidate, null) ?? candidate);
    }
}
