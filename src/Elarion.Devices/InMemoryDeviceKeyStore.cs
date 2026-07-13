using System.Collections.Concurrent;

namespace Elarion.Devices;

/// <summary>
/// In-memory <see cref="IDeviceKeyStore"/> for tests and single-node development. Keys vanish on
/// restart — production uses the EF-backed store (<c>Elarion.Devices.EntityFrameworkCore</c>),
/// which is why registration is an explicit opt-in, never a silent default.
/// </summary>
public sealed class InMemoryDeviceKeyStore : IDeviceKeyStore {
    private readonly ConcurrentDictionary<string, byte[]> _keys = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<ReadOnlyMemory<byte>?> GetKeyAsync(string deviceId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        // Convert on the non-null branch only: a null byte[] would otherwise become an EMPTY
        // ReadOnlyMemory and report the device as known.
        return ValueTask.FromResult(
            _keys.TryGetValue(deviceId, out var key) ? (ReadOnlyMemory<byte>?)key : null);
    }

    /// <inheritdoc />
    public ValueTask PutAsync(string deviceId, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        _keys[deviceId] = key.ToArray();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(string deviceId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        return ValueTask.FromResult(_keys.TryRemove(deviceId, out _));
    }
}
