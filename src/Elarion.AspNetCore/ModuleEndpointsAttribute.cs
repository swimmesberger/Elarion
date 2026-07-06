namespace Elarion.AspNetCore;

/// <summary>
/// Declares endpoint hooks for a module from outside its assembly — typically in the host, on behalf of a module
/// whose own assembly is deliberately web-free (no shared-framework reference, so the module type cannot declare
/// <c>MapEndpoints</c>/<c>ConfigureEndpointGroup</c> itself). <c>Elarion.Generators.AppModuleDiscoveryGenerator</c>
/// discovers the annotated static class and calls its hooks inside the module's feature gate in
/// <c>MapElarionEndpoints</c>, exactly like hooks declared on the <c>[AppModule]</c> type: a disabled module's
/// contributed endpoints disappear with it, with no hand-written <c>IsModuleEnabled</c> re-check.
/// </summary>
/// <remarks>
/// The class declares the same convention-based static hooks a module type may declare, both optional (but at
/// least one must be present — otherwise <c>ELMOD005</c>):
/// <list type="bullet">
/// <item><c>static void MapEndpoints(IEndpointRouteBuilder endpoints)</c> — maps hand-written routes for the module.</item>
/// <item><c>static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder endpoints)</c> — wraps the
/// builder the module's endpoints (including its generated <c>[HttpEndpoint]</c> routes) are mapped onto, e.g.
/// <c>endpoints.MapGroup("/import-export").RequireAuthorization()</c>.</item>
/// </list>
/// The module's own hooks run first; contributors follow in stable type-name order, their group hooks chained
/// onto the module's. <paramref name="moduleName"/> must match a discovered <c>[AppModule]</c> name — an unknown
/// name is skipped with <c>ELMOD004</c>. Contributors are discovered in the host compilation and, via the
/// per-assembly Elarion manifest, in referenced assemblies (e.g. a web companion assembly beside a web-free
/// module assembly).
/// </remarks>
/// <example>
/// <code>
/// [ModuleEndpoints("ImportExport")]
/// internal static class ImportExportEndpoints {
///     public static void MapEndpoints(IEndpointRouteBuilder endpoints) =>
///         endpoints.MapGet("export/{liste}", static async (string liste, IMasterDataExporter exporter, CancellationToken ct) =>
///             ElarionHttpResults.ToFileResult(await exporter.ExportAsync(liste, ct)));
/// }
/// </code>
/// </example>
/// <param name="moduleName">The <c>[AppModule]</c> name the hooks contribute to (e.g. <c>"ImportExport"</c>).</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ModuleEndpointsAttribute(string moduleName) : Attribute {
    /// <summary>The <c>[AppModule]</c> name the annotated class contributes endpoints to.</summary>
    public string ModuleName { get; } = moduleName;
}
