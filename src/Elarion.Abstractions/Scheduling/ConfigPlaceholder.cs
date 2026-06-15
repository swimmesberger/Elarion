using Microsoft.Extensions.Configuration;

namespace Elarion.Abstractions.Scheduling;

/// <summary>
/// Resolves Spring-style configuration placeholders in scheduling attribute values:
/// <c>${Section:Key}</c> reads a configuration value, <c>${Section:Key:-fallback}</c>
/// supplies an inline default for when the key is not configured. Any other text is
/// returned unchanged as a literal.
/// </summary>
public static class ConfigPlaceholder {
    /// <summary>True when the value is a <c>${...}</c> placeholder rather than a literal.</summary>
    public static bool IsPlaceholder(string value) =>
        value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}');

    /// <summary>
    /// Resolves a placeholder against configuration, or returns the literal unchanged.
    /// Returns null when the key is not configured and no inline default exists.
    /// </summary>
    /// <exception cref="FormatException">The placeholder has an empty key.</exception>
    public static string? Resolve(string value, IConfiguration configuration) {
        if (!IsPlaceholder(value)) {
            // Note 16: Literal values and placeholders share the same public API, so schedule attributes stay string-based.
            return value;
        }

        var body = value[2..^1];
        // Note 17: The ":-" separator intentionally follows Spring property-placeholder syntax.
        var defaultSeparatorIndex = body.IndexOf(":-", StringComparison.Ordinal);
        var key = defaultSeparatorIndex >= 0 ? body[..defaultSeparatorIndex] : body;
        var inlineDefault = defaultSeparatorIndex >= 0 ? body[(defaultSeparatorIndex + 2)..] : null;
        if (string.IsNullOrWhiteSpace(key)) {
            throw new FormatException($"Configuration placeholder '{value}' has an empty key.");
        }

        var configured = configuration[key];
        // Note 18: Empty configured values behave like missing values so inline defaults can recover from blank settings.
        return string.IsNullOrWhiteSpace(configured) ? inlineDefault : configured;
    }

    /// <summary>
    /// Resolves like <see cref="Resolve"/> but treats an unresolvable placeholder as an error.
    /// </summary>
    /// <exception cref="InvalidOperationException">The key is not configured and has no inline default.</exception>
    public static string ResolveRequired(string value, IConfiguration configuration) =>
        Resolve(value, configuration) ?? throw new InvalidOperationException(
            $"Configuration placeholder '{value}' resolved to nothing: the key is not configured and no inline ':-' default was provided.");
}
