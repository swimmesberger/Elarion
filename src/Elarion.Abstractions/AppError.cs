namespace Elarion.Abstractions;

/// <summary>
/// Categorizes the kind of application error.
/// The transport layer is responsible for mapping these to protocol-specific codes.
/// </summary>
public enum ErrorKind {
    /// <summary>Invalid input or constraint violation.</summary>
    Validation,
    /// <summary>The requested resource does not exist.</summary>
    NotFound,
    /// <summary>The operation conflicts with existing state (e.g., duplicate, concurrent modification).</summary>
    Conflict,
    /// <summary>The caller is not authorized to perform this operation.</summary>
    Forbidden,
    /// <summary>A domain business rule was violated.</summary>
    BusinessRule,
    /// <summary>An unexpected internal error occurred.</summary>
    Internal,
    /// <summary>The caller is not authenticated (no/invalid credentials). Maps to HTTP 401.</summary>
    Unauthorized,
}

/// <summary>
/// Represents a structured application error with a semantic kind, message, and optional data payload.
/// Used as the failure type in <see cref="Result{T}"/> — transport-agnostic.
/// The API layer maps <see cref="Kind"/> to transport-specific codes (e.g., JSON-RPC integer codes).
/// </summary>
/// <example>
/// <code>
/// return AppError.NotFound($"Client {id} not found");
/// return AppError.Validation("Name is required");
/// </code>
/// </example>
public sealed record AppError {
    /// <summary>The semantic category of this error.</summary>
    public required ErrorKind Kind { get; init; }
    /// <summary>A human-readable description of the error.</summary>
    public required string Message { get; init; }
    /// <summary>Optional structured data providing additional context.</summary>
    public object? Data { get; init; }

    /// <summary>An internal error singleton for unexpected exceptions.</summary>
    public static readonly AppError InternalError = new() { Kind = ErrorKind.Internal, Message = "Internal error" };

    /// <summary>Creates a validation error with optional details.</summary>
    public static AppError Validation(string message, object? data = null) =>
        new() { Kind = ErrorKind.Validation, Message = message, Data = data };

    /// <summary>Creates a validation error with a list of error messages as data.</summary>
    public static AppError Validation(string message, IReadOnlyList<string> errors) =>
        new() { Kind = ErrorKind.Validation, Message = message, Data = new ValidationErrorData { Errors = errors } };

    /// <summary>
    /// Creates a validation error carrying messages keyed by wire-named field path (e.g.
    /// <c>"address.street"</c>; the empty-string key is for messages not specific to a field). The flat
    /// <see cref="ValidationErrorData.Errors"/> list is derived by flattening in ordinal key order, so
    /// consumers of either shape see the same messages.
    /// </summary>
    public static AppError Validation(string message, IReadOnlyDictionary<string, string[]> fieldErrors) =>
        new() {
            Kind = ErrorKind.Validation,
            Message = message,
            Data = new ValidationErrorData {
                Errors = ValidationErrorData.Flatten(fieldErrors),
                FieldErrors = fieldErrors,
            },
        };

    /// <summary>Creates a not-found error.</summary>
    public static AppError NotFound(string message) =>
        new() { Kind = ErrorKind.NotFound, Message = message };

    /// <summary>Creates a conflict error (e.g., duplicate, concurrent modification).</summary>
    public static AppError Conflict(string message) =>
        new() { Kind = ErrorKind.Conflict, Message = message };

    /// <summary>Creates a forbidden/authorization error (authenticated but not permitted).</summary>
    public static AppError Forbidden(string message) =>
        new() { Kind = ErrorKind.Forbidden, Message = message };

    /// <summary>Creates an unauthorized/authentication error (no or invalid credentials).</summary>
    public static AppError Unauthorized(string message) =>
        new() { Kind = ErrorKind.Unauthorized, Message = message };

    /// <summary>Creates a business rule violation error with optional details.</summary>
    public static AppError BusinessRule(string message, object? data = null) =>
        new() { Kind = ErrorKind.BusinessRule, Message = message, Data = data };

    /// <summary>Creates an internal error with an optional detail message.</summary>
    public static AppError Internal(string message, object? data = null) =>
        new() { Kind = ErrorKind.Internal, Message = message, Data = data };
}

/// <summary>Structured data payload for validation errors.</summary>
public sealed record ValidationErrorData {
    /// <summary>Human-readable validation messages.</summary>
    public required IReadOnlyList<string> Errors { get; init; }

    /// <summary>
    /// Validation messages keyed by wire-named field path (e.g. <c>"address.street"</c>, matching the property
    /// names the client sent per the canonical JSON naming policy); the empty-string key carries messages not
    /// specific to a single field. <see langword="null"/> when the error was built from a flat message list.
    /// </summary>
    public IReadOnlyDictionary<string, string[]>? FieldErrors { get; init; }

    /// <summary>Flattens field-keyed messages into one list, in deterministic (ordinal key) order.</summary>
    internal static string[] Flatten(IReadOnlyDictionary<string, string[]> fieldErrors) =>
        [.. fieldErrors.OrderBy(static pair => pair.Key, StringComparer.Ordinal).SelectMany(static pair => pair.Value)];
}
