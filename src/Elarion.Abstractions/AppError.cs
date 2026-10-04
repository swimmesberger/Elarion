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
    Unauthorized
}

/// <summary>
/// Represents a structured application error: a semantic <see cref="Kind"/>, a stable machine-readable
/// <see cref="Code"/>, a human-readable <see cref="Message"/> and an optional typed <see cref="Data"/> payload.
/// Used as the failure type in <see cref="Result{T}"/> — transport-agnostic.
/// The API layer maps <see cref="Kind"/> to transport-specific status codes (for example JSON-RPC integer codes)
/// and carries <see cref="Code"/> and <see cref="Data"/> on every transport.
/// </summary>
/// <example>
/// <code>
/// return AppError.NotFound($"Client {id} not found");
/// return AppError.Conflict("The seat is taken.", code: "seat.taken", data: new SeatTaken(seatId));
/// </code>
/// </example>
public sealed record AppError {
    private readonly string? _code;

    /// <summary>The semantic category of this error; it drives the transport status mapping.</summary>
    public required ErrorKind Kind { get; init; }

    /// <summary>A human-readable description of the error. Prose, localizable and not a contract; branch on <see cref="Code"/>.</summary>
    public required string Message { get; init; }

    /// <summary>
    /// The stable machine-readable identifier of this failure (for example <c>"token.malformed"</c>); always
    /// present. An error created without a specific code carries its kind's default code
    /// (<see cref="ErrorCodes.ForKind"/>, for example <c>"not_found"</c>). A code is a public contract — see
    /// <see cref="ErrorCodes"/> for the format. Handlers declare the codes they produce with
    /// <see cref="ProducesErrorAttribute"/>.
    /// </summary>
    public string Code {
        get => _code ?? ErrorCodes.ForKind(Kind);
        // A null or empty value means "unset" (the kind's default code): a deserializer hands a missing member to a
        // required initializer as empty. Factories validate an explicit code themselves.
        init => _code = string.IsNullOrEmpty(value) ? null : ErrorCodes.ThrowIfInvalid(value);
    }

    /// <summary>
    /// Optional typed payload providing structured context for this <see cref="Code"/>; carried on every
    /// transport as the error's <c>data</c>.
    /// </summary>
    /// <remarks>
    /// A payload type that crosses a wire transport must be registered in a source-generated
    /// <c>JsonSerializerContext</c> the host composes into the canonical JSON options; the payload is serialized by
    /// its runtime type and an unregistered type fails on an AOT-strict host. Declare the payload type with
    /// <see cref="ProducesErrorAttribute"/> so the schema and the generated clients describe it.
    /// </remarks>
    public object? Data { get; init; }

    /// <summary>An internal error singleton for unexpected exceptions.</summary>
    public static readonly AppError InternalError = new() { Kind = ErrorKind.Internal, Message = "Internal error" };

    /// <summary>Two errors are equal when kind, message, effective code and payload are equal.</summary>
    public bool Equals(AppError? other) {
        return other is not null
               && Kind == other.Kind
               && Message == other.Message
               && Code == other.Code
               && Equals(Data, other.Data);
    }

    /// <inheritdoc />
    public override int GetHashCode() {
        return HashCode.Combine(Kind, Message, Code, Data);
    }

    /// <summary>Creates an error of <paramref name="kind"/>.</summary>
    /// <param name="kind">The semantic category.</param>
    /// <param name="message">The human-readable description.</param>
    /// <param name="code">A specific stable code, or <see langword="null"/> for the kind's default code.</param>
    /// <param name="data">The optional typed payload.</param>
    public static AppError Create(ErrorKind kind, string message, string? code = null, object? data = null) {
        return code is null
            ? new AppError { Kind = kind, Message = message, Data = data }
            : new AppError { Kind = kind, Message = message, Code = ErrorCodes.ThrowIfInvalid(code), Data = data };
    }

    /// <summary>Creates a validation error with optional details.</summary>
    public static AppError Validation(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.Validation, message, code, data);
    }

    /// <summary>Creates a validation error with a list of error messages as data.</summary>
    public static AppError Validation(string message, IReadOnlyList<string> errors, string? code = null) {
        return Create(ErrorKind.Validation, message, code, new ValidationErrorData { Errors = errors });
    }

    /// <summary>
    /// Creates a validation error carrying messages keyed by wire-named field path (e.g.
    /// <c>"address.street"</c>; the empty-string key is for messages not specific to a field). The flat
    /// <see cref="ValidationErrorData.Errors"/> list is derived by flattening in ordinal key order, so
    /// consumers of either shape see the same messages.
    /// </summary>
    public static AppError Validation(
        string message, IReadOnlyDictionary<string, string[]> fieldErrors, string? code = null) {
        return Create(ErrorKind.Validation, message, code, new ValidationErrorData {
            Errors = ValidationErrorData.Flatten(fieldErrors),
            FieldErrors = fieldErrors
        });
    }

    /// <summary>Creates a not-found error.</summary>
    public static AppError NotFound(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.NotFound, message, code, data);
    }

    /// <summary>Creates a conflict error (e.g., duplicate, concurrent modification).</summary>
    public static AppError Conflict(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.Conflict, message, code, data);
    }

    /// <summary>Creates a forbidden/authorization error (authenticated but not permitted).</summary>
    public static AppError Forbidden(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.Forbidden, message, code, data);
    }

    /// <summary>Creates an unauthorized/authentication error (no or invalid credentials).</summary>
    public static AppError Unauthorized(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.Unauthorized, message, code, data);
    }

    /// <summary>Creates a business rule violation error with optional details.</summary>
    public static AppError BusinessRule(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.BusinessRule, message, code, data);
    }

    /// <summary>Creates an internal error with an optional detail message.</summary>
    public static AppError Internal(string message, string? code = null, object? data = null) {
        return Create(ErrorKind.Internal, message, code, data);
    }
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
    internal static string[] Flatten(IReadOnlyDictionary<string, string[]> fieldErrors) {
        return [
            .. fieldErrors.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .SelectMany(static pair => pair.Value)
        ];
    }
}
