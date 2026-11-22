namespace Elarion.AspNetCore.OpenApi;

/// <summary>Well-known Elarion OpenAPI vendor-extension keys placed on generated operations.</summary>
public static class ElarionOpenApiExtensionNames {
    /// <summary>
    /// Marks an operation whose handler is <c>[Idempotent]</c>. Set to <see langword="true"/>; the machine-readable
    /// analog of the JSON-RPC schema's <c>idempotent</c> flag, so a generated client can auto-attach an
    /// <c>Idempotency-Key</c> header for the operation.
    /// </summary>
    public const string Idempotent = "x-elarion-idempotent";

    /// <summary>
    /// Marks a string schema whose value is the id of a file staged through the upload endpoints
    /// (<see cref="Elarion.Abstractions.UploadedFile"/>, ADR-0105): a client uploads the file first and sends the id.
    /// </summary>
    public const string Upload = "x-elarion-upload";

    /// <summary>
    /// The largest size in bytes an <see cref="Upload"/> member accepts, from its
    /// <see cref="Elarion.Abstractions.UploadLimitsAttribute"/> (ADR-0100); the JSON-RPC schema uses the same keyword.
    /// </summary>
    public const string MaxBytes = "x-elarion-max-bytes";

    /// <summary>
    /// The media types an <see cref="Upload"/> member accepts, from its
    /// <see cref="Elarion.Abstractions.UploadLimitsAttribute"/> (ADR-0100); the JSON-RPC schema uses the same keyword.
    /// </summary>
    public const string ContentTypes = "x-elarion-content-types";

    /// <summary>
    /// The file-name extensions an <see cref="Upload"/> member accepts (for example <c>".xlsx"</c>), from its
    /// <see cref="Elarion.Abstractions.UploadLimitsAttribute"/> (ADR-0105); the JSON-RPC schema uses the same keyword.
    /// </summary>
    public const string FileExtensions = "x-elarion-file-extensions";
}
