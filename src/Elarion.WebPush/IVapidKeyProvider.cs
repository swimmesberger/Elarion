using Microsoft.Extensions.Logging;

namespace Elarion.WebPush;

/// <summary>Resolves the VAPID key pair deliveries are signed with and browsers subscribe against.</summary>
public interface IVapidKeyProvider {
    /// <summary>The key pair. Stable for the life of the process.</summary>
    /// <param name="cancellationToken">Cancels a first-use store read or write.</param>
    ValueTask<VapidKeys> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IVapidKeyProvider"/>: configured keys (<see cref="WebPushOptions.PublicKey"/>/
/// <see cref="WebPushOptions.PrivateKey"/>) win; otherwise the <see cref="IVapidKeyStore"/>'s pair, generated
/// and stored race-safely on first use. The resolved pair is cached; a failed resolution is retried on the
/// next call.
/// </summary>
internal sealed class VapidKeyProvider(
    WebPushOptions options,
    IVapidKeyStore store,
    ILogger<VapidKeyProvider> logger) : IVapidKeyProvider, IDisposable {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VapidKeys? _keys;

    public async ValueTask<VapidKeys> GetAsync(CancellationToken cancellationToken = default) {
        if (Volatile.Read(ref _keys) is { } cached) return cached;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_keys is not null) return _keys;
            var keys = await ResolveAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _keys, keys);
            return keys;
        }
        finally {
            _gate.Release();
        }
    }

    public void Dispose() {
        _gate.Dispose();
    }

    private async ValueTask<VapidKeys> ResolveAsync(CancellationToken cancellationToken) {
        if (!string.IsNullOrEmpty(options.PublicKey))
            // Validated at registration (WebPushOptions.Validate).
            return new VapidKeys { PublicKey = options.PublicKey, PrivateKey = options.PrivateKey! };

        var stored = await store.GetAsync(cancellationToken).ConfigureAwait(false);
        if (stored is not null) return stored;

        var winner = await store.GetOrAddAsync(VapidKeys.Generate(), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Generated and stored the Web Push VAPID key pair (public key {PublicKey}).",
            winner.PublicKey);
        return winner;
    }
}
