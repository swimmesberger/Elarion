namespace Elarion.WebPush;

/// <summary>
/// Durable storage for the one VAPID key pair. The default <see cref="IVapidKeyProvider"/> reads it, and on
/// first use generates a candidate and offers it through <see cref="GetOrAddAsync"/>, so nodes racing the
/// first start agree on one pair. An application that already holds a pair elsewhere imports it with
/// <see cref="ImportAsync"/>.
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

    /// <summary>
    /// Stores an existing key pair (for example one migrated from another system, so browsers' subscriptions keep
    /// working) under the store's own protection. A no-op when the identical pair is already stored. When a
    /// <em>different</em> pair is stored the import is refused unless <paramref name="overwrite"/> is set, because
    /// replacing the pair invalidates every browser subscription. Must be atomic across nodes: concurrent imports
    /// of different pairs leave exactly one stored and refuse the others.
    /// </summary>
    /// <remarks>
    /// The default <see cref="IVapidKeyProvider"/> caches the pair for the life of the process: import before the
    /// first use (typically in a migration or startup step), and restart other nodes after an overwrite.
    /// </remarks>
    /// <param name="keys">The key pair to store; its halves must belong together.</param>
    /// <param name="overwrite">Replace a different stored pair instead of refusing.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">The pair is malformed or its halves do not match.</exception>
    ValueTask<VapidKeyImportResult> ImportAsync(
        VapidKeys keys, bool overwrite = false, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="IVapidKeyStore.ImportAsync"/>.</summary>
public enum VapidKeyImportResult {
    /// <summary>No pair was stored; the imported one is now.</summary>
    Imported,

    /// <summary>The identical pair was already stored; nothing changed.</summary>
    Unchanged,

    /// <summary>A different pair was stored and was replaced (<c>overwrite</c> was set).</summary>
    Replaced,

    /// <summary>A different pair is stored and <c>overwrite</c> was not set; nothing changed.</summary>
    Refused
}
