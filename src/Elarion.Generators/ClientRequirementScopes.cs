namespace Elarion.Generators;

/// <summary>
/// Merges the authentication an <c>[ElarionAuthorizationDefaults]</c> requires into each operation's client
/// metadata. The default lives on the module class or the assembly, outside the handler, so — like
/// <see cref="ErrorContractScopes"/> — this is a second stage over the discovered operations, combined with the shared
/// module provider and the current compilation, and it resolves the default with the same code the handler
/// registration generator uses to attach the authorization decorator, so the exported requirement and the enforced
/// one cannot disagree.
/// </summary>
internal static class ClientRequirementScopes {
    /// <summary>
    /// Returns <paramref name="models"/> with <c>RequiresAuthentication</c> set on every current-compilation operation
    /// a default in scope guards. A model decoded from a referenced manifest already carries its resolved value.
    /// </summary>
    public static EquatableArray<RpcMethodEmission.Model> Apply(
        EquatableArray<RpcMethodEmission.Model> models,
        EquatableArray<ModuleScanner.Module> modules,
        Microsoft.CodeAnalysis.Compilation compilation,
        CancellationToken ct) {
        var assemblyDefault = HandlerRegistrationGenerator.ResolveAssemblyAuthorizationDefault(compilation);
        var moduleDefaults = HandlerRegistrationGenerator.BuildModuleMaps(compilation, modules, ct).AuthDefaults;
        if (!assemblyDefault && moduleDefaults.Count == 0)
            return models;

        var result = new List<RpcMethodEmission.Model>(models.Length);
        foreach (var model in models) {
            ct.ThrowIfCancellationRequested();
            if (model is { HandlerMetadataName: not null, Client: { AllowAnonymous: false, RequiresAuthentication: false } client }
                && HandlerRegistrationGenerator.ResolveAuthorizationDefault(
                    model.HandlerNamespace, moduleDefaults, assemblyDefault))
                result.Add(model with { Client = client with { RequiresAuthentication = true } });
            else
                result.Add(model);
        }

        return result.ToEquatableArray();
    }
}
