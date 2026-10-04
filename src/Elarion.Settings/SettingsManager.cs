using System.Text.Json;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings;

/// <summary>
/// Default <see cref="ISettingsManager"/> over the <see cref="ISettingResolver"/>, the
/// <see cref="ISettingsStore"/> and an <see cref="ISettingsChangeSource"/>. Registered scoped so it observes the
/// current request's <c>ICurrentUser</c>; the user is resolved lazily through <see cref="IServiceProvider"/>
/// (mirroring <c>HybridHandlerCache</c>) so global-only usage does not require an <c>ICurrentUser</c> registration.
/// </summary>
public sealed class SettingsManager(
    ISettingDefinitionCatalog catalog,
    ISettingResolver resolver,
    ISettingPins pins,
    ISettingsStore store,
    ISettingsChangeSource changeSource,
    IElarionJsonSerialization jsonSerialization,
    IServiceProvider services,
    ISettingValueProtector? protector = null) : ISettingsManager {
    private readonly SettingValueCodec _codec = new(protector);

    /// <inheritdoc />
    public async ValueTask<T> GetAsync<T>(
        SettingDefinition<T> definition,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default) {
        return (await GetResolvedAsync(definition, scope, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <inheritdoc />
    public async ValueTask<SettingValue<T>> GetResolvedAsync<T>(
        SettingDefinition<T> definition,
        SettingsScope? scope = null,
        CancellationToken cancellationToken = default) {
        var resolved = await resolver.ResolveAsync(definition, ResolveScope(scope), cancellationToken)
            .ConfigureAwait(false);
        if (resolved.ValueJson is null)
            return new SettingValue<T>(definition.Default, resolved.Source, resolved.Version, resolved.IsPinned);

        try {
            var value = JsonSerializer.Deserialize(resolved.ValueJson, jsonSerialization.GetTypeInfo<T>());
            return new SettingValue<T>(value is null ? definition.Default : value, resolved.Source, resolved.Version,
                resolved.IsPinned);
        }
        catch (JsonException ex) {
            throw new InvalidOperationException(
                $"The {resolved.Source.ToString().ToLowerInvariant()} value of setting '{definition.Key}' is not a " +
                $"valid {typeof(T).Name}.", ex);
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<SettingWrite>> SetAsync<T>(
        SettingDefinition<T> definition,
        T value,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        var resolvedScope = ResolveScope(scope);
        if (CheckWritable(definition, resolvedScope) is { } refused) return refused;

        var json = JsonSerializer.Serialize(value, jsonSerialization.GetTypeInfo<T>());
        var (stored, protection) = _codec.Encode(definition, resolvedScope, json);
        var result = await store.SetAsync(resolvedScope, definition.Key, stored, protection, expectedVersion,
            cancellationToken).ConfigureAwait(false);

        return result.IsSuccess
            ? Result<SettingWrite>.Success(new SettingWrite(result.Version))
            : ConcurrencyConflict(definition);
    }

    /// <inheritdoc />
    public async ValueTask<Result<bool>> ResetAsync(
        SettingDefinition definition,
        SettingsScope? scope = null,
        int? expectedVersion = null,
        CancellationToken cancellationToken = default) {
        var resolvedScope = ResolveScope(scope);
        if (CheckWritable(definition, resolvedScope) is { } refused) return Result<bool>.Failure(refused.Error);

        var removed = await store.RemoveAsync(resolvedScope, definition.Key, expectedVersion, cancellationToken)
            .ConfigureAwait(false);
        if (!removed && expectedVersion is not null)
            return Result<bool>.Failure(ConcurrencyConflict(definition).Error);

        return removed;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<SettingDescription>> DescribeAsync(
        SettingsScope? scope = null,
        string? keyPrefix = null,
        CancellationToken cancellationToken = default) {
        var resolvedScope = ResolveScope(scope);
        var resolved = await resolver.ResolveAllAsync(resolvedScope, keyPrefix, cancellationToken)
            .ConfigureAwait(false);

        var descriptions = new List<SettingDescription>(resolved.Count);
        foreach (var setting in resolved) {
            var definition = setting.Definition;
            var valueJson = definition.IsSecret || setting.IsUnreadable ? null
                : setting.Source == SettingSource.Default ? definition.SerializeDefault(jsonSerialization)
                : setting.ValueJson;

            descriptions.Add(new SettingDescription {
                Key = definition.Key,
                ValueType = definition.ValueType,
                Scope = resolvedScope,
                AllowedScopes = definition.Scopes,
                IsSecret = definition.IsSecret,
                IsPinnable = definition.IsPinnable,
                IsPinned = setting.IsPinned,
                HasValue = setting.HasValue,
                Source = setting.Source,
                ValueJson = valueJson,
                Version = setting.Version,
                IsUnreadable = setting.IsUnreadable,
                UnreadableReason = setting.UnreadableReason,
                Description = definition.Description
            });
        }

        return descriptions;
    }

    /// <inheritdoc />
    public IChangeToken Watch(SettingDefinition definition, SettingsScope? scope = null) {
        catalog.Require(definition);
        var resolvedScope = ResolveScope(scope);
        return Combine(changeSource.Watch(resolvedScope, definition.Key), resolvedScope, definition.IsPinnable);
    }

    /// <inheritdoc />
    public IChangeToken Watch(string? keyPrefix = null, SettingsScope? scope = null) {
        var resolvedScope = ResolveScope(scope);
        var anyPinnable = catalog.All.Any(d => d.IsPinnable && SettingsPath.IsUnderPrefix(d.Key, keyPrefix));
        return Combine(changeSource.Watch(resolvedScope, keyPrefix), resolvedScope, anyPinnable);
    }

    private IChangeToken Combine(IChangeToken storeToken, SettingsScope scope, bool includeConfiguration) {
        if (!includeConfiguration || scope.Kind != SettingsScope.GlobalKind ||
            pins.GetConfigurationChangeToken() is not { } configurationToken)
            return storeToken;

        return new CompositeChangeToken([storeToken, configurationToken]);
    }

    private Result<SettingWrite>? CheckWritable(SettingDefinition definition, SettingsScope scope) {
        catalog.Require(definition);
        if (!definition.AllowsScope(scope.Kind))
            throw new InvalidOperationException(
                $"Setting '{definition.Key}' does not allow the '{scope.Kind}' scope " +
                $"(allowed: {string.Join(", ", definition.Scopes)}).");

        if (!pins.IsPinned(definition, scope)) return null;

        return Result<SettingWrite>.Failure(AppError.Create(
            ErrorKind.BusinessRule,
            $"Setting '{definition.Key}' is pinned by deployment configuration and cannot be changed " +
            "at runtime.",
            SettingErrorCodes.Pinned,
            new SettingWriteFailure(definition.Key)));
    }

    private static Result<SettingWrite> ConcurrencyConflict(SettingDefinition definition) {
        return Result<SettingWrite>.Failure(AppError.Create(
            ErrorKind.Conflict,
            $"Setting '{definition.Key}' was changed concurrently; its version no longer matches.",
            SettingErrorCodes.ConcurrencyConflict,
            new SettingWriteFailure(definition.Key)));
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
