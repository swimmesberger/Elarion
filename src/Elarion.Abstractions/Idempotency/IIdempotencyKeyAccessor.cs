using System.Diagnostics.CodeAnalysis;

namespace Elarion.Abstractions.Idempotency;

/// <summary>
/// Scoped access to the idempotency key captured at the transport boundary for the current call — the
/// idempotency analog of <see cref="Elarion.Abstractions.Identity.ICurrentUser"/>. Seeded per call through the
/// dispatch-scope rail (an <see cref="Elarion.Abstractions.Dispatch.IDispatchScopeInitializer"/>), so the
/// decorator reads it without any transport coupling.
/// </summary>
public interface IIdempotencyKeyAccessor {
    /// <summary>Gets the idempotency key for the current call, if one was captured.</summary>
    /// <param name="key">The captured key, or <see langword="null"/> when none was supplied.</param>
    /// <returns><see langword="true"/> when a non-empty key was captured.</returns>
    bool TryGetKey([NotNullWhen(true)] out string? key);
}

/// <summary>
/// A write seam for the per-call idempotency key, so a transport can seed the key it read from an in-band
/// location (a JSON-RPC/MCP <c>params._meta</c> field) directly into the scope after it is created — without the
/// dispatch-scope rail (which would re-run every initializer). Resolves to the same scoped instance as
/// <see cref="IIdempotencyKeyAccessor"/>, and overrides any key seeded from the transport boundary.
/// </summary>
public interface IIdempotencyKeySeed {
    /// <summary>
    /// Sets the idempotency key for the current call scope. <see langword="null"/> clears a previously seeded
    /// key — a reused dispatch scope (per-connection dispatch) re-seeds per message, and a message without a
    /// key must not inherit the previous message's.
    /// </summary>
    void Seed(string? key);
}
