using System.Text.Json;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// Default <see cref="ISettingsManager"/> over an <see cref="ISettingsStore"/> and an
/// <see cref="ISettingsChangeSource"/>. Registered scoped so it observes the current request's
/// <c>ICurrentUser</c>; the user is resolved lazily through <see cref="IServiceProvider"/> (mirroring
/// <c>HybridHandlerCache</c>) so global-only usage does not require an <c>ICurrentUser</c> registration.
/// </summary>
public sealed class SettingsManager(
    ISettingsStore store,
    ISettingsChangeSource changeSource,
    IElarionJsonSerialization jsonSerialization,
    IServiceProvider services) : ISettingsManager {
    /// <inheritdoc />
    public async ValueTask<T> GetAsync<T>(
        string key,
        T fallback,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default) {
        var raw = await store.GetAsync(ResolveScope(scope), key, cancellationToken).ConfigureAwait(false);
        if (raw is null) return fallback;

        var value = JsonSerializer.Deserialize(raw, jsonSerialization.GetTypeInfo<T>());
        return value is null ? fallback : value;
    }

    /// <inheritdoc />
    public ValueTask<SettingWriteResult> SetAsync<T>(
        string key,
        T value,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        var raw = JsonSerializer.Serialize(value, jsonSerialization.GetTypeInfo<T>());
        return store.SetAsync(ResolveScope(scope), key, raw, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<string?> GetStringAsync(
        string key,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default) {
        return store.GetAsync(ResolveScope(scope), key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<SettingWriteResult> SetStringAsync(
        string key,
        string? value,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        return store.SetAsync(ResolveScope(scope), key, value, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> RemoveAsync(
        string key,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        return store.RemoveAsync(ResolveScope(scope), key, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public IChangeToken Watch(string? keyPrefix = null, SettingsScope? scope = null) {
        return changeSource.Watch(ResolveScope(scope), keyPrefix);
    }

    private SettingsScope ResolveScope(SettingsScope? scope) {
        var resolved = scope ?? SettingsScope.Global;
        if (!resolved.IsCurrentUserPlaceholder) return resolved;

        // Resolve the ambient user lazily and fail closed when unauthenticated (for example outside HTTP).
        var currentUser = services.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.UserId))
            throw new InvalidOperationException(
                "User-scoped settings require an authenticated current user with a user id.");

        return SettingsScope.User(currentUser.UserId);
    }
}
