using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Validation;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;

namespace Elarion.Validation;

/// <summary>
/// The default <see cref="IRequestValidator"/> over <c>Microsoft.Extensions.Validation</c>: resolves the
/// request type's validation metadata from <see cref="ValidationOptions"/> (populated by the registered
/// <see cref="IValidatableInfoResolver"/>s), runs it, and translates the CLR property paths of any violations
/// into wire-named field paths through the canonical JSON naming policy (ADR-0023) — so error keys match the
/// property names the client sent.
/// </summary>
/// <remarks>
/// Scoped: the <see cref="ValidationContext"/> is built over the current scope's <see cref="IServiceProvider"/>,
/// so custom <see cref="ValidationAttribute"/>s and <see cref="IValidatableObject"/> implementations can resolve
/// scoped services. A request type no resolver knows is valid by definition (<see langword="null"/>), so
/// unannotated requests cost one dictionary probe.
/// </remarks>
public sealed class MicrosoftRequestValidator(
    IOptions<ValidationOptions> validationOptions,
    IServiceProvider serviceProvider,
    IElarionJsonSerialization jsonSerialization
) : IRequestValidator {
    /// <inheritdoc />
    public async ValueTask<RequestValidationErrors?> ValidateAsync(Type requestType, object request, CancellationToken cancellationToken) {
        var options = validationOptions.Value;
        if (!options.TryGetValidatableTypeInfo(requestType, out var validatableInfo)) {
            return null;
        }

        // The explicit-display-name constructor is the trim-safe one (no reflection over DisplayNameAttribute);
        // the validation walker overwrites DisplayName per member before each check anyway.
        var context = new ValidateContext {
            ValidationContext = new ValidationContext(request, requestType.Name, serviceProvider, items: null),
            ValidationOptions = options,
        };

        await validatableInfo.ValidateAsync(request, context, cancellationToken).ConfigureAwait(false);

        if (context.ValidationErrors is not { Count: > 0 } errors) {
            return null;
        }

        var namingPolicy = jsonSerialization.Options.PropertyNamingPolicy;
        var fieldErrors = new Dictionary<string, string[]>(errors.Count, StringComparer.Ordinal);
        foreach (var (path, messages) in errors) {
            // Two CLR paths can collide on one wire path (e.g. "Name" and "name" both camelCase to "name");
            // merge rather than overwrite so no violation is silently dropped.
            var wirePath = TranslatePath(path, namingPolicy);
            fieldErrors[wirePath] = fieldErrors.TryGetValue(wirePath, out var existing)
                ? [.. existing, .. messages]
                : messages;
        }

        return new RequestValidationErrors { FieldErrors = fieldErrors };
    }

    /// <summary>
    /// Translates a CLR property path (e.g. <c>"Address.Street"</c>, <c>"Items[0].Name"</c>) into its wire-named
    /// form by converting each segment's property-name part through <paramref name="namingPolicy"/>, preserving
    /// indexer contents verbatim — a dictionary key inside <c>[...]</c> may itself contain dots (e.g.
    /// <c>"Items[My.Key].Name"</c>) and must never be split or case-converted. A <see langword="null"/> policy
    /// leaves the path unchanged.
    /// </summary>
    private static string TranslatePath(string path, JsonNamingPolicy? namingPolicy) {
        if (namingPolicy is null || path.Length == 0) {
            return path;
        }

        var builder = new StringBuilder(path.Length);
        var index = 0;
        while (index < path.Length) {
            var nameStart = index;
            while (index < path.Length && path[index] != '.' && path[index] != '[') {
                index++;
            }

            if (index > nameStart) {
                builder.Append(namingPolicy.ConvertName(path[nameStart..index]));
            }

            while (index < path.Length && path[index] == '[') {
                var close = path.IndexOf(']', index);
                if (close < 0) {
                    // Unterminated indexer: emit the remainder verbatim rather than mangling it.
                    builder.Append(path, index, path.Length - index);
                    return builder.ToString();
                }

                builder.Append(path, index, close - index + 1);
                index = close + 1;
            }

            if (index < path.Length && path[index] == '.') {
                builder.Append('.');
                index++;
            }
        }

        return builder.ToString();
    }
}
