namespace Elarion.Abstractions.Validation;

/// <summary>
/// The violations an <see cref="IRequestValidator"/> found on a request, keyed by wire-named field path.
/// </summary>
public sealed record RequestValidationErrors {
    /// <summary>
    /// Validation messages keyed by wire-named field path (e.g. <c>"address.street"</c>, using the canonical
    /// JSON naming policy so keys match the property names the client sent). The empty-string key carries
    /// messages that are not specific to a single field.
    /// </summary>
    public required IReadOnlyDictionary<string, string[]> FieldErrors { get; init; }
}
