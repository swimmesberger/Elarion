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
}
