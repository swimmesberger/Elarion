namespace Elarion.Abstractions;

/// <summary>
/// The stable, machine-readable error codes of the framework: one default code per <see cref="ErrorKind"/> plus
/// the codes the pipeline itself produces. Every <see cref="AppError"/> carries a code; an error built without a
/// specific one carries its kind's default code.
/// </summary>
/// <remarks>
/// A code is a public contract: lower-case ASCII letters, digits and underscores in dot-separated segments (for
/// example <c>"token.malformed"</c>), never reused for a different meaning and never renamed once shipped. Handlers
/// declare the codes they can produce with <see cref="ProducesErrorAttribute"/>.
/// </remarks>
public static class ErrorCodes {
    /// <summary>Default code of <see cref="ErrorKind.Validation"/>.</summary>
    public const string Validation = "validation";

    /// <summary>Default code of <see cref="ErrorKind.NotFound"/>.</summary>
    public const string NotFound = "not_found";

    /// <summary>Default code of <see cref="ErrorKind.Conflict"/>.</summary>
    public const string Conflict = "conflict";

    /// <summary>Default code of <see cref="ErrorKind.Forbidden"/>.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>Default code of <see cref="ErrorKind.Unauthorized"/>.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>Default code of <see cref="ErrorKind.BusinessRule"/>.</summary>
    public const string BusinessRule = "business_rule";

    /// <summary>Default code of <see cref="ErrorKind.Internal"/>.</summary>
    public const string Internal = "internal";

    /// <summary>
    /// The default code of <see cref="ErrorKind.DecisionRequired"/>. A decision normally carries its own code (the one
    /// its handler declares with <see cref="RequiresDecisionAttribute"/>); this is the fallback.
    /// </summary>
    public const string DecisionRequired = "decision_required";

    /// <summary>A mutating call to an <c>[Idempotent]</c> operation carried no idempotency key (<see cref="ErrorKind.Validation"/>).</summary>
    public const string IdempotencyKeyRequired = "idempotency.key_required";

    /// <summary>A request with this idempotency key is still being processed (<see cref="ErrorKind.Conflict"/>).</summary>
    public const string IdempotencyInProgress = "idempotency.in_progress";

    /// <summary>The idempotency key was already used with a different request (<see cref="ErrorKind.BusinessRule"/>).</summary>
    public const string IdempotencyKeyReused = "idempotency.key_reused";

    /// <summary>
    /// A previewable command's outcome no longer matches the preview the caller confirmed; nothing was committed and
    /// the error's data is the new outcome (<see cref="ErrorKind.Conflict"/>).
    /// </summary>
    public const string PreviewChanged = "preview.changed";

    /// <summary>
    /// A previewable command was asked to preview, or to commit against a preview, but the host has no unit of work
    /// that can roll back; the handler did not run (<see cref="ErrorKind.Internal"/>).
    /// </summary>
    public const string PreviewUnsupported = "preview.unsupported";

    /// <summary>
    /// An <see cref="UploadedFile"/> names no pending upload of the caller: it is missing, was removed by the blob
    /// garbage collector, was already kept, belongs to someone else or lies outside the upload containers — all one
    /// answer, so an id cannot be probed (<see cref="ErrorKind.Validation"/>).
    /// </summary>
    public const string UploadNotFound = "upload.not_found";

    /// <summary>An <see cref="UploadedFile"/> is larger than <see cref="UploadConstraints.MaxBytes"/> (<see cref="ErrorKind.Validation"/>).</summary>
    public const string UploadTooLarge = "upload.too_large";

    /// <summary>
    /// An <see cref="UploadedFile"/>'s content type is not one of <see cref="UploadConstraints.ContentTypes"/>
    /// (<see cref="ErrorKind.Validation"/>).
    /// </summary>
    public const string UploadContentType = "upload.content_type";

    /// <summary>Returns the default code of <paramref name="kind"/>.</summary>
    public static string ForKind(ErrorKind kind) {
        return kind switch {
            ErrorKind.Validation => Validation,
            ErrorKind.NotFound => NotFound,
            ErrorKind.Conflict => Conflict,
            ErrorKind.Forbidden => Forbidden,
            ErrorKind.Unauthorized => Unauthorized,
            ErrorKind.BusinessRule => BusinessRule,
            ErrorKind.DecisionRequired => DecisionRequired,
            _ => Internal
        };
    }

    /// <summary>
    /// Whether <paramref name="code"/> is well formed: dot-separated, non-empty segments of lower-case ASCII
    /// letters, digits and underscores, the first character a letter.
    /// </summary>
    public static bool IsValid(string? code) {
        if (string.IsNullOrEmpty(code) || code[0] is < 'a' or > 'z') return false;

        var segmentStart = true;
        foreach (var character in code) {
            if (character == '.') {
                if (segmentStart) return false;

                segmentStart = true;
                continue;
            }

            if (character is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_')) return false;

            segmentStart = false;
        }

        return !segmentStart;
    }

    internal static string ThrowIfInvalid(string? code) {
        if (!IsValid(code))
            throw new ArgumentException(
                $"'{code}' is not a valid error code: use lower-case ASCII letters, digits and underscores in dot-separated segments (for example \"token.malformed\").",
                nameof(code));

        return code!;
    }
}
