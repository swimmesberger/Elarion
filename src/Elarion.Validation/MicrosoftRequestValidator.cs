using System.ComponentModel.DataAnnotations;
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
            fieldErrors[TranslatePath(path, namingPolicy)] = messages;
        }

        return new RequestValidationErrors { FieldErrors = fieldErrors };
    }

    /// <summary>
    /// Translates a CLR property path (e.g. <c>"Address.Street"</c>, <c>"Items[0].Name"</c>) into its wire-named
    /// form by converting each segment's property-name part through <paramref name="namingPolicy"/>, preserving
    /// indexer suffixes. A <see langword="null"/> policy leaves the path unchanged.
    /// </summary>
    private static string TranslatePath(string path, JsonNamingPolicy? namingPolicy) {
        if (namingPolicy is null || path.Length == 0) {
            return path;
        }

        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++) {
            var segment = segments[i];
            var bracket = segment.IndexOf('[');
            var name = bracket < 0 ? segment : segment[..bracket];
            if (name.Length == 0) {
                continue;
            }

            segments[i] = bracket < 0
                ? namingPolicy.ConvertName(name)
                : namingPolicy.ConvertName(name) + segment[bracket..];
        }

        return string.Join('.', segments);
    }
}
