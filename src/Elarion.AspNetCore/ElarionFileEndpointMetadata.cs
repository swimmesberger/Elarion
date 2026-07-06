namespace Elarion.AspNetCore;

/// <summary>
/// Inert endpoint metadata attached by <c>Elarion.Generators.AppModuleDiscoveryGenerator</c> to a generated
/// <c>[HttpEndpoint]</c> whose handler responds with <c>Result&lt;ElarionFile&gt;</c>. It carries no behavior:
/// the file translation is <see cref="ElarionHttpResults.ToFileResult"/> in the generated lambda. The marker
/// exists so <c>Elarion.AspNetCore.OpenApi</c> can document the response as a binary payload
/// (<c>type: string, format: binary</c>) without the generator taking an OpenAPI dependency — a host without
/// the OpenAPI package simply ignores it.
/// </summary>
/// <remarks>
/// A single shared <see cref="Instance"/> is used because the marker is a pure presence flag; the OpenAPI
/// operation transformer detects it via <c>context.Description.ActionDescriptor.EndpointMetadata.OfType&lt;…&gt;()</c>.
/// </remarks>
public sealed class ElarionFileEndpointMetadata {
    /// <summary>The shared marker instance emitted onto every generated file-response endpoint.</summary>
    public static readonly ElarionFileEndpointMetadata Instance = new();

    private ElarionFileEndpointMetadata() { }
}
