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
