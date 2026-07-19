using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Idempotency;

namespace Elarion.Idempotency;

/// <summary>
/// The default scoped <see cref="IIdempotencyKeyAccessor"/>: holds the key seeded for the current dispatch
/// scope by <see cref="IdempotencyKeyScopeInitializer"/>. Mirrors <c>ClaimsPrincipalCurrentUser</c>.
/// </summary>
internal sealed class ScopedIdempotencyKeyAccessor : IIdempotencyKeyAccessor, IIdempotencyKeySeed {
    private string? _key;

    /// <summary>Sets the key for this scope — from the scope initializer, or a transport's in-band seed.
    /// <see langword="null"/> clears it (a reused per-connection scope re-seeds per message).</summary>
    public void Seed(string? key) {
        _key = key;
    }

    /// <inheritdoc />
    public bool TryGetKey([NotNullWhen(true)] out string? key) {
        key = _key;
        return !string.IsNullOrEmpty(_key);
    }
}
