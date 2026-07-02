namespace Elarion.AspNetCore;

/// <summary>
/// Inert endpoint metadata attached by <c>Elarion.Generators.AppModuleDiscoveryGenerator</c> to a generated
/// <c>[HttpEndpoint]</c> whose handler is <c>[Idempotent]</c>. It carries no behavior: the HTTP idempotency
/// enforcement is the <c>IdempotencyDecorator</c> in the handler pipeline (fed the key by
/// <c>UseElarionIdempotencyKey</c>). The marker exists so <c>Elarion.AspNetCore.OpenApi</c> can advertise the
/// <c>Idempotency-Key</c> header and the <c>x-elarion-idempotent</c> extension on the operation without the
/// generator taking an OpenAPI dependency — a host without the OpenAPI package simply ignores it.
/// </summary>
/// <remarks>
/// A single shared <see cref="Instance"/> is used because the marker is a pure presence flag; the OpenAPI operation
/// transformer detects it via <c>context.Description.ActionDescriptor.EndpointMetadata.OfType&lt;…&gt;()</c>.
/// </remarks>
public sealed class ElarionIdempotentEndpointMetadata {
    /// <summary>The shared marker instance emitted onto every idempotent generated endpoint.</summary>
    public static readonly ElarionIdempotentEndpointMetadata Instance = new();

    private ElarionIdempotentEndpointMetadata() { }
}
