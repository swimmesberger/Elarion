using Elarion.Abstractions.Serialization;

namespace Elarion.Abstractions.Settings;

/// <summary>Descriptive metadata of a setting definition, supplied by the generated declaration.</summary>
public sealed class SettingDefinitionOptions {
    /// <summary>The allowed scope kinds. <see langword="null"/> or empty means global only.</summary>
    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Whether the value is protected at rest and hidden from describe and configuration projection.</summary>
    public bool IsSecret { get; init; }

    /// <summary>Whether deployment configuration may pin the value.</summary>
    public bool IsPinnable { get; init; }

    /// <summary>A human-readable description for admin tooling.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// A declared setting: key, value type, default, allowed scopes, and the secret/pinnable flags. Instances are
/// created by the source generator for <see cref="SettingAttribute"/> properties; the runtime resolves and writes
/// settings only through registered definitions.
/// </summary>
public abstract class SettingDefinition {
    private static readonly string[] GlobalOnly = ["global"];

    private protected SettingDefinition(string key, Type valueType, SettingDefinitionOptions options) {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(valueType);
        ArgumentNullException.ThrowIfNull(options);

        Key = key;
        ValueType = valueType;
        Scopes = options.Scopes is { Count: > 0 } scopes ? [.. scopes] : GlobalOnly;
        IsSecret = options.IsSecret;
        IsPinnable = options.IsPinnable;
        Description = options.Description;
    }

    /// <summary>The hierarchical key.</summary>
    public string Key { get; }

    /// <summary>The CLR type of the value.</summary>
    public Type ValueType { get; }

    /// <summary>The scope kinds the setting may be read and written in.</summary>
    public IReadOnlyList<string> Scopes { get; }

    /// <summary>Whether the value is protected at rest and hidden from describe and configuration projection.</summary>
    public bool IsSecret { get; }

    /// <summary>Whether deployment configuration may pin the value.</summary>
    public bool IsPinnable { get; }

    /// <summary>A human-readable description, if declared.</summary>
    public string? Description { get; }

    /// <summary>Whether the definition declares a default (otherwise the default is <c>default(T)</c>).</summary>
    public abstract bool HasDefault { get; }

    /// <summary>Whether the setting may be accessed in a scope of the given kind.</summary>
    public bool AllowsScope(string scopeKind) {
        ArgumentNullException.ThrowIfNull(scopeKind);
        foreach (var scope in Scopes)
            if (string.Equals(scope, scopeKind, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>Serializes the default value to canonical JSON text, or <see langword="null"/> when there is none.</summary>
    public abstract string? SerializeDefault(IElarionJsonSerialization serialization);
}

/// <summary>The typed definition of a setting whose value is <typeparamref name="T"/>.</summary>
/// <typeparam name="T">The value type; must be serializable by the canonical JSON options.</typeparam>
public sealed class SettingDefinition<T> : SettingDefinition {
    private readonly Func<T>? _defaultFactory;
    private readonly bool _hasDefault;
    private readonly Lock _gate = new();
    private T? _default;
    private bool _defaultReady;

    /// <summary>Creates a definition without a declared default. Generated code calls this.</summary>
    public SettingDefinition(string key, SettingDefinitionOptions options)
        : base(key, typeof(T), options) {
    }

    /// <summary>Creates a definition with a constant default. Generated code calls this.</summary>
    public SettingDefinition(string key, SettingDefinitionOptions options, T defaultValue)
        : base(key, typeof(T), options) {
        _default = defaultValue;
        _defaultReady = true;
        _hasDefault = true;
    }

    /// <summary>Creates a definition whose default is produced lazily, once. Generated code calls this.</summary>
    public SettingDefinition(string key, SettingDefinitionOptions options, Func<T> defaultFactory)
        : base(key, typeof(T), options) {
        _defaultFactory = defaultFactory ?? throw new ArgumentNullException(nameof(defaultFactory));
        _hasDefault = true;
    }

    /// <inheritdoc />
    public override bool HasDefault => _hasDefault;

    /// <summary>The declared default, or <c>default(T)</c> when none is declared.</summary>
    public T Default {
        get {
            if (_defaultReady) return _default!;

            lock (_gate) {
                if (!_defaultReady) {
                    _default = _defaultFactory is null ? default : _defaultFactory();
                    _defaultReady = true;
                }
            }

            return _default!;
        }
    }

    /// <inheritdoc />
    public override string? SerializeDefault(IElarionJsonSerialization serialization) {
        ArgumentNullException.ThrowIfNull(serialization);
        return _hasDefault
            ? System.Text.Json.JsonSerializer.Serialize(Default, serialization.GetTypeInfo<T>())
            : null;
    }
}
