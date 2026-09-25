namespace Elarion.WebPush;

/// <summary>
/// Durable storage for the one VAPID key pair. The default <see cref="IVapidKeyProvider"/> reads it, and on
/// first use generates a candidate and offers it through <see cref="GetOrAddAsync"/>, so nodes racing the
/// first start agree on one pair.
/// </summary>
public interface IVapidKeyStore {
    /// <summary>The stored key pair, or <see langword="null"/> before one was ever stored.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<VapidKeys?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores <paramref name="candidate"/> unless a pair already exists, and returns whichever pair is stored
    /// afterwards. Must be atomic across nodes: two concurrent callers with different candidates both get
    /// the same winner back.
    /// </summary>
    /// <param name="candidate">A freshly generated key pair.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask<VapidKeys> GetOrAddAsync(VapidKeys candidate, CancellationToken cancellationToken = default);
}
