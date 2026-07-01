using System.Diagnostics.CodeAnalysis;
using Elarion.Abstractions.Idempotency;

namespace Elarion.Idempotency;

/// <summary>
/// The default scoped <see cref="IIdempotencyKeyAccessor"/>: holds the key seeded for the current dispatch
/// scope by <see cref="IdempotencyKeyScopeInitializer"/>. Mirrors <c>ClaimsPrincipalCurrentUser</c>.
/// </summary>
internal sealed class ScopedIdempotencyKeyAccessor : IIdempotencyKeyAccessor {
    private string? _key;

    /// <summary>Sets the key for this scope. Called once per dispatch by the scope initializer.</summary>
    internal void Initialize(string? key) => _key = key;

    /// <inheritdoc />
    public bool TryGetKey([NotNullWhen(true)] out string? key) {
        key = _key;
        return !string.IsNullOrEmpty(_key);
    }
}
