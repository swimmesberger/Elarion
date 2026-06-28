using System.Text;

namespace Elarion.Abstractions.Substitution;

/// <summary>
/// Spring-style variable substitution, reusable across subsystems (it is not tied to any one feature). A
/// placeholder is <c>${key}</c>, or <c>${key:-default}</c> to supply an inline default when the key is unset.
/// Values come from a pluggable <see cref="IVariableSource"/>.
/// </summary>
/// <remarks>
/// Two models are supported. The <b>whole-value</b> model (<see cref="IsPlaceholder"/>, <see cref="Resolve"/>,
/// <see cref="ResolveRequired"/>) treats a string as either a literal or a single placeholder — useful for
/// optional, settable attributes (an unresolved placeholder yields <see langword="null"/>). The
/// <b>embedded</b> model (<see cref="Substitute"/>) replaces every <c>${...}</c> occurrence inside a larger
/// template string, like a connection string or a path. Nesting (<c>${a:${b}}</c>) is not supported.
/// </remarks>
public static class VariableSubstitution {
    private const string Open = "${";
    private const char Close = '}';
    private const string DefaultSeparator = ":-";

    /// <summary>True when the entire value is a single <c>${...}</c> placeholder rather than a literal.</summary>
    public static bool IsPlaceholder(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return value.StartsWith(Open, StringComparison.Ordinal) && value.EndsWith(Close);
    }

    /// <summary>True when the value contains at least one <c>${...}</c> placeholder anywhere.</summary>
    public static bool ContainsPlaceholder(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return value.Contains(Open, StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves a whole-value placeholder against <paramref name="source"/>, or returns a literal unchanged.
    /// Returns <see langword="null"/> when the placeholder's key is unset and it carries no inline default.
    /// </summary>
    /// <exception cref="FormatException">The placeholder has an empty key.</exception>
    public static string? Resolve(string value, IVariableSource source) {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(source);
        if (!IsPlaceholder(value)) {
            // Literals and placeholders share one API, so attribute values can stay string-based.
            return value;
        }

        var (key, inlineDefault) = ParseBody(value[Open.Length..^1], value);
        return ResolveValue(key, inlineDefault, source);
    }

    /// <summary>Resolves like <see cref="Resolve"/> but treats an unresolvable placeholder as an error.</summary>
    /// <exception cref="InvalidOperationException">The key is unset and there is no inline default.</exception>
    public static string ResolveRequired(string value, IVariableSource source) =>
        Resolve(value, source) ?? throw new InvalidOperationException(
            $"Variable placeholder '{value}' resolved to nothing: the key is not set and no inline ':-' default was provided.");

    /// <summary>
    /// Replaces every <c>${...}</c> placeholder inside <paramref name="template"/> with its resolved value,
    /// leaving the surrounding text intact. An unterminated <c>${</c> is emitted verbatim.
    /// </summary>
    /// <exception cref="FormatException">A placeholder has an empty key.</exception>
    /// <exception cref="InvalidOperationException">A placeholder's key is unset and it has no inline default.</exception>
    public static string Substitute(string template, IVariableSource source) {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(source);

        var start = template.IndexOf(Open, StringComparison.Ordinal);
        if (start < 0) {
            // Fast path: nothing to substitute, no allocation.
            return template;
        }

        var builder = new StringBuilder(template.Length);
        var index = 0;
        while (start >= 0) {
            var end = template.IndexOf(Close, start + Open.Length);
            if (end < 0) {
                // Unterminated placeholder: emit the remainder verbatim.
                break;
            }

            builder.Append(template, index, start - index);
            var body = template[(start + Open.Length)..end];
            var (key, inlineDefault) = ParseBody(body, template);
            builder.Append(ResolveValue(key, inlineDefault, source) ?? throw new InvalidOperationException(
                $"Variable placeholder '{Open}{body}{Close}' in '{template}' resolved to nothing: '{key}' is not set and no inline ':-' default was provided."));

            index = end + 1;
            start = template.IndexOf(Open, index, StringComparison.Ordinal);
        }

        builder.Append(template, index, template.Length - index);
        return builder.ToString();
    }

    private static string? ResolveValue(string key, string? inlineDefault, IVariableSource source) {
        var resolved = source.TryGetValue(key, out var value) ? value : null;
        // Empty/whitespace values behave like missing so an inline default can recover from a blank setting.
        return string.IsNullOrWhiteSpace(resolved) ? inlineDefault : resolved;
    }

    private static (string Key, string? Default) ParseBody(string body, string original) {
        var separatorIndex = body.IndexOf(DefaultSeparator, StringComparison.Ordinal);
        var key = separatorIndex >= 0 ? body[..separatorIndex] : body;
        var inlineDefault = separatorIndex >= 0 ? body[(separatorIndex + DefaultSeparator.Length)..] : null;
        if (string.IsNullOrWhiteSpace(key)) {
            throw new FormatException($"Variable placeholder '{original}' has an empty key.");
        }

        return (key, inlineDefault);
    }
}
