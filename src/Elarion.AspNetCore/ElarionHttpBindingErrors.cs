using Microsoft.AspNetCore.Http;

namespace Elarion.AspNetCore;

/// <summary>
/// Accumulated binding failures for one generated <c>[HttpEndpoint]</c> request, passed by <see langword="ref"/>
/// through the <see cref="ElarionHttpEndpointBinder"/> helpers. A mutable struct so the happy path allocates
/// nothing: <see langword="default"/> is the clean state, and the error dictionary only materializes on the
/// first failure.
/// </summary>
public struct ElarionHttpBindingErrors {
    private Dictionary<string, string[]>? _errors;
    private int _statusCode;

    /// <summary>Whether any binding failure has been recorded; the generated code gates handler invocation on this.</summary>
    public readonly bool HasErrors => _errors is not null || _statusCode != 0;

    internal void AddMissing(string name) {
        Add(name, $"The {name} field is required.");
    }

    internal void AddInvalid(string name, string? raw) {
        Add(name, $"The value '{raw}' is not valid for {name}.");
    }

    internal void Add(string key, string message) {
        _errors ??= new Dictionary<string, string[]>(StringComparer.Ordinal);
        _errors[key] = _errors.TryGetValue(key, out var existing)
            ? [.. existing, message]
            : [message];
    }

    internal void SetStatus(int statusCode) {
        _statusCode = statusCode;
    }

    /// <summary>
    /// Writes the accumulated failures: a bare status for 415, everything else as the RFC 7807
    /// <c>ValidationProblem</c> the handler-tier validation failures also use.
    /// </summary>
    public readonly Task WriteAsync(HttpContext httpContext) {
        if (_statusCode == StatusCodes.Status415UnsupportedMediaType)
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType).ExecuteAsync(httpContext);

        var errors = _errors ?? new Dictionary<string, string[]>(StringComparer.Ordinal);
        return Results.ValidationProblem(errors).ExecuteAsync(httpContext);
    }
}
