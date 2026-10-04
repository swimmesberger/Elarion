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

    /// <inheritdoc />
    public ValueTask<VapidKeyImportResult> ImportAsync(
        VapidKeys keys, bool overwrite = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Validate();
        while (true) {
            var current = Volatile.Read(ref _keys);
            if (current is not null && Same(current, keys)) return ValueTask.FromResult(VapidKeyImportResult.Unchanged);

            if (current is not null && !overwrite) return ValueTask.FromResult(VapidKeyImportResult.Refused);

            if (Interlocked.CompareExchange(ref _keys, keys, current) == current)
                return ValueTask.FromResult(current is null ? VapidKeyImportResult.Imported : VapidKeyImportResult.Replaced);
        }
    }

    internal static bool Same(VapidKeys left, VapidKeys right) {
        return string.Equals(left.PublicKey, right.PublicKey, StringComparison.Ordinal)
               && string.Equals(left.PrivateKey, right.PrivateKey, StringComparison.Ordinal);
    }
}
