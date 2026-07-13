using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

internal static class ElarionManifest
{
    public const string SchemaKey = "Elarion.Manifest.Schema";
    public const string SchemaVersion = "1";

    /// <summary>
    /// A referenced assembly advertises an Elarion manifest whose schema version this generator does not
    /// understand. Its entries (modules, HTTP/RPC endpoints, permission-catalog entries) are skipped rather than
    /// misparsed — reported loudly because dropping permission/role entries silently would weaken authorization.
    /// </summary>
    public static readonly DiagnosticDescriptor UnsupportedManifestSchema = new(
        id: "ELMOD003",
        title: "Unsupported Elarion manifest schema version",
        messageFormat:
            "Referenced assembly '{0}' advertises Elarion manifest schema version '{1}', but this generator "
            + "understands version '{2}'; its manifest entries (modules, endpoints, permissions) are skipped. "
            + "Rebuild the reference against a matching Elarion version.",
        category: "Elarion.Modules",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
    public const string ModuleKey = "Elarion.Manifest.Module.v1";
    public const string ModuleEndpointsKey = "Elarion.Manifest.ModuleEndpoints.v1";
    public const string HttpEndpointKey = "Elarion.Manifest.HttpEndpoint.v1";
    public const string RpcMethodKey = "Elarion.Manifest.RpcMethod.v1";
    public const string ResourceFilterKey = "Elarion.Manifest.ResourceFilter.v1";
    public const string PermissionKey = "Elarion.Manifest.Permission.v1";
    public const string RoleKey = "Elarion.Manifest.Role.v1";
    public const string VariantKey = "Elarion.Manifest.Variant.v1";

    // A [RequirePermission(resource, verb)]/[RequireRole] declared by a handler, carried so the host-side
    // ElarionPermissions static can aggregate the permission catalog across referenced module assemblies.
    // Namespace is the declaring handler's namespace (the static resolves the owning module by longest prefix).
    public sealed record Permission(string Namespace, string Resource, string Verb);

    public sealed record Role(string Namespace, string Value);

    // A [FeatureVariant]/[ConfigurationVariant] implementation's contribution to the variant registry (one per
    // contract), carried so the host-side ElarionVariants static aggregates switches across referenced
    // assemblies. Namespace is the implementation's namespace (the static resolves the owning module — or
    // "platform" for none — by longest prefix); ContractIsPublic gates the aggregating assembly's typeof()
    // emission (an internal contract still contributes its key/values, but no Type).
    public sealed record Variant(
        string Namespace,
        bool IsConfiguration,
        string SelectorKey,
        string ContractFqn,
        string? Value,
        bool IsDefault,
        bool ContractIsPublic
    );

    // A generated [ResourceFilter] data-level authorizer that the host bootstrapper registers as
    // IQueryAuthorizer<TEntity>. The emitted-member contract: a non-shared spec exposes a static
    // `Specification` singleton (registered AddSingleton); a shared spec is a scoped service with a
    // grant-source constructor (registered AddScoped). See ADR-0014.
    public sealed record ResourceFilter(
        string SpecFqn,
        string EntityFqn,
        string Namespace,
        bool IsShared
    );

    public sealed record Module(
        string ModuleName,
        string Namespace,
        string TypeFqn,
        string? DependsOn,
        bool IsCore,
        bool HasConfigureServices,
        bool HasMapEndpoints,
        bool HasGetJsonTypeInfoResolver,
        bool HasConfigureEndpointGroup,
        EquatableArray<string> ClientFeatures
    );

    // A [ModuleEndpoints("Name")] static class contributing endpoint hooks to a module from another assembly
    // (typically a host or web companion assembly beside a web-free module assembly). The host bootstrapper
    // calls its MapEndpoints/ConfigureEndpointGroup hooks inside the named module's feature gate.
    public sealed record ModuleEndpoints(
        string ModuleName,
        string TypeFqn,
        bool HasMapEndpoints,
        bool HasConfigureEndpointGroup
    );

    public sealed record Data(
        IReadOnlyList<Module> Modules,
        IReadOnlyList<ModuleEndpoints> ModuleEndpointHooks,
        IReadOnlyList<HttpEndpointEmission.Model> HttpEndpoints,
        IReadOnlyList<RpcMethodEmission.Model> RpcMethods,
        IReadOnlyList<ResourceFilter> ResourceFilters,
        IReadOnlyList<Permission> Permissions,
        IReadOnlyList<Role> Roles,
        IReadOnlyList<Variant> Variants
    )
    {
        public static readonly Data Empty = new([], [], [], [], [], [], [], []);

        public bool HasEntries =>
            Modules.Count > 0 || ModuleEndpointHooks.Count > 0 || HttpEndpoints.Count > 0 || RpcMethods.Count > 0
            || ResourceFilters.Count > 0 || Permissions.Count > 0 || Roles.Count > 0 || Variants.Count > 0;

        public static Data Combine(IEnumerable<Data> manifests)
        {
            var modules = new List<Module>();
            var moduleEndpointHooks = new List<ModuleEndpoints>();
            var httpEndpoints = new List<HttpEndpointEmission.Model>();
            var rpcMethods = new List<RpcMethodEmission.Model>();
            var resourceFilters = new List<ResourceFilter>();
            var permissions = new List<Permission>();
            var roles = new List<Role>();
            var variants = new List<Variant>();

            foreach (var manifest in manifests)
            {
                modules.AddRange(manifest.Modules);
                moduleEndpointHooks.AddRange(manifest.ModuleEndpointHooks);
                httpEndpoints.AddRange(manifest.HttpEndpoints);
                rpcMethods.AddRange(manifest.RpcMethods);
                resourceFilters.AddRange(manifest.ResourceFilters);
                permissions.AddRange(manifest.Permissions);
                roles.AddRange(manifest.Roles);
                variants.AddRange(manifest.Variants);
            }

            modules.Sort(static (a, b) =>
            {
                var byName = string.Compare(a.ModuleName, b.ModuleName, StringComparison.Ordinal);
                if (byName != 0)
                    return byName;

                return string.Compare(a.TypeFqn, b.TypeFqn, StringComparison.Ordinal);
            });

            moduleEndpointHooks.Sort(static (a, b) =>
            {
                var byName = string.Compare(a.ModuleName, b.ModuleName, StringComparison.Ordinal);
                if (byName != 0)
                    return byName;

                return string.Compare(a.TypeFqn, b.TypeFqn, StringComparison.Ordinal);
            });

            httpEndpoints.Sort(static (a, b) =>
            {
                var byVerb = string.Compare(a.Verb, b.Verb, StringComparison.Ordinal);
                if (byVerb != 0)
                    return byVerb;
                var byRoute = string.Compare(a.Route, b.Route, StringComparison.Ordinal);
                if (byRoute != 0)
                    return byRoute;
                return string.Compare(a.EndpointName, b.EndpointName, StringComparison.Ordinal);
            });

            rpcMethods.Sort(static (a, b) =>
            {
                var byMethod = string.Compare(a.MethodName, b.MethodName, StringComparison.Ordinal);
                if (byMethod != 0)
                    return byMethod;
                return string.Compare(a.RequestTypeFqn, b.RequestTypeFqn, StringComparison.Ordinal);
            });

            resourceFilters.Sort(static (a, b) => string.Compare(a.SpecFqn, b.SpecFqn, StringComparison.Ordinal));

            permissions.Sort(static (a, b) =>
            {
                var byResource = string.Compare(a.Resource, b.Resource, StringComparison.Ordinal);
                if (byResource != 0)
                    return byResource;
                var byVerb = string.Compare(a.Verb, b.Verb, StringComparison.Ordinal);
                return byVerb != 0 ? byVerb : string.Compare(a.Namespace, b.Namespace, StringComparison.Ordinal);
            });

            roles.Sort(static (a, b) =>
            {
                var byValue = string.Compare(a.Value, b.Value, StringComparison.Ordinal);
                return byValue != 0 ? byValue : string.Compare(a.Namespace, b.Namespace, StringComparison.Ordinal);
            });

            variants.Sort(static (a, b) =>
            {
                var byKey = string.Compare(a.SelectorKey, b.SelectorKey, StringComparison.Ordinal);
                if (byKey != 0)
                    return byKey;
                var byContract = string.Compare(a.ContractFqn, b.ContractFqn, StringComparison.Ordinal);
                if (byContract != 0)
                    return byContract;
                return string.Compare(a.Value, b.Value, StringComparison.Ordinal);
            });

            return new Data(
                modules, moduleEndpointHooks, httpEndpoints, rpcMethods, resourceFilters, permissions, roles,
                variants);
        }
    }

    public static string EncodeModule(Module module) =>
        ElarionManifestCodec.EncodeFields(
            module.ModuleName,
            module.Namespace,
            module.TypeFqn,
            module.DependsOn,
            EncodeBool(module.IsCore),
            EncodeBool(module.HasConfigureServices),
            EncodeBool(module.HasMapEndpoints),
            EncodeBool(module.HasGetJsonTypeInfoResolver),
            EncodeBool(module.HasConfigureEndpointGroup),
            // The exposed client-feature names ride a nested length-prefixed blob (like RPC parameters), so a
            // name containing a separator can never corrupt the outer field framing.
            ElarionManifestCodec.EncodeFields([.. module.ClientFeatures]));

    public static string EncodeModuleEndpoints(ModuleEndpoints hooks) =>
        ElarionManifestCodec.EncodeFields(
            hooks.ModuleName,
            hooks.TypeFqn,
            EncodeBool(hooks.HasMapEndpoints),
            EncodeBool(hooks.HasConfigureEndpointGroup));

    public static bool TryDecodeModuleEndpoints(string value, out ModuleEndpoints? hooks)
    {
        hooks = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 4)
            return false;
        if (fields[0] is null || fields[1] is null)
            return false;
        if (!TryDecodeBool(fields[2], out var hasMapEndpoints) ||
            !TryDecodeBool(fields[3], out var hasConfigureEndpointGroup))
        {
            return false;
        }

        hooks = new ModuleEndpoints(fields[0]!, fields[1]!, hasMapEndpoints, hasConfigureEndpointGroup);
        return true;
    }

    public static string EncodeHttpEndpoint(HttpEndpointEmission.Model model) =>
        ElarionManifestCodec.EncodeFields(
            model.EndpointName,
            model.HandlerNamespace,
            model.RequestTypeFqn,
            model.ResponseTypeFqn,
            model.Route,
            model.Verb,
            EncodeBool(model.UseAsParameters),
            EncodeBool(model.DisableAntiforgery),
            EncodeBool(model.ResponseIsEmpty),
            model.Description,
            EncodeBool(model.IsIdempotent));

    public static string EncodeRpcMethod(RpcMethodEmission.Model model) =>
        ElarionManifestCodec.EncodeFields(
            model.MethodName,
            model.HandlerNamespace,
            model.RequestTypeFqn,
            model.ResponseTypeFqn,
            model.ToolName,
            EncodeBool(model.OnJsonRpc),
            EncodeBool(model.OnMcp),
            model.Description,
            ElarionManifestCodec.EncodeParameters(model.Parameters),
            EncodeBool(model.IsNameInferred),
            EncodeBool(model.IsIdempotent),
            // Appended (not inserted) so earlier field indices stay stable; count-gated decode below.
            EncodeBool(model.OnConnection));

    public static string EncodeResourceFilter(ResourceFilter filter) =>
        ElarionManifestCodec.EncodeFields(
            filter.SpecFqn,
            filter.EntityFqn,
            filter.Namespace,
            EncodeBool(filter.IsShared));

    public static bool TryDecodeResourceFilter(string value, out ResourceFilter? filter)
    {
        filter = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 4)
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null)
            return false;
        if (!TryDecodeBool(fields[3], out var isShared))
            return false;

        filter = new ResourceFilter(fields[0]!, fields[1]!, fields[2]!, isShared);
        return true;
    }

    public static string EncodePermission(Permission permission) =>
        ElarionManifestCodec.EncodeFields(permission.Namespace, permission.Resource, permission.Verb);

    public static bool TryDecodePermission(string value, out Permission? permission)
    {
        permission = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 3)
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null)
            return false;

        permission = new Permission(fields[0]!, fields[1]!, fields[2]!);
        return true;
    }

    public static string EncodeVariant(Variant variant) =>
        ElarionManifestCodec.EncodeFields(
            variant.Namespace,
            EncodeBool(variant.IsConfiguration),
            variant.SelectorKey,
            variant.ContractFqn,
            variant.Value,
            EncodeBool(variant.IsDefault),
            EncodeBool(variant.ContractIsPublic));

    public static bool TryDecodeVariant(string value, out Variant? variant)
    {
        variant = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 7)
            return false;
        if (fields[0] is null || fields[2] is null || fields[3] is null)
            return false;
        if (!TryDecodeBool(fields[1], out var isConfiguration) ||
            !TryDecodeBool(fields[5], out var isDefault) ||
            !TryDecodeBool(fields[6], out var contractIsPublic))
        {
            return false;
        }

        variant = new Variant(
            fields[0]!,
            isConfiguration,
            fields[2]!,
            fields[3]!,
            fields[4],
            isDefault,
            contractIsPublic);
        return true;
    }

    public static string EncodeRole(Role role) =>
        ElarionManifestCodec.EncodeFields(role.Namespace, role.Value);

    public static bool TryDecodeRole(string value, out Role? role)
    {
        role = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 2)
            return false;
        if (fields[0] is null || fields[1] is null)
            return false;

        role = new Role(fields[0]!, fields[1]!);
        return true;
    }

    public static bool TryDecodeModule(string value, out Module? module)
    {
        module = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 10)
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null)
            return false;
        if (!TryDecodeBool(fields[4], out var isCore) ||
            !TryDecodeBool(fields[5], out var hasConfigureServices) ||
            !TryDecodeBool(fields[6], out var hasMapEndpoints) ||
            !TryDecodeBool(fields[7], out var hasGetJsonTypeInfoResolver) ||
            !TryDecodeBool(fields[8], out var hasConfigureEndpointGroup))
        {
            return false;
        }

        var clientFeatures = EquatableArray<string>.Empty;
        if (fields[9] is { Length: > 0 } encodedFeatures &&
            ElarionManifestCodec.TryDecodeFields(encodedFeatures, out var featureFields))
        {
            var names = new List<string>();
            foreach (var name in featureFields)
            {
                if (name is not null)
                    names.Add(name);
            }

            clientFeatures = names.ToEquatableArray();
        }

        module = new Module(
            fields[0]!,
            fields[1]!,
            fields[2]!,
            fields[3],
            isCore,
            hasConfigureServices,
            hasMapEndpoints,
            hasGetJsonTypeInfoResolver,
            hasConfigureEndpointGroup,
            clientFeatures);
        return true;
    }

    public static bool TryDecodeHttpEndpoint(string value, out HttpEndpointEmission.Model? model)
    {
        model = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 11)
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null || fields[3] is null ||
            fields[4] is null || fields[5] is null)
        {
            return false;
        }

        if (!TryDecodeBool(fields[6], out var useAsParameters) ||
            !TryDecodeBool(fields[7], out var disableAntiforgery) ||
            !TryDecodeBool(fields[8], out var responseIsEmpty) ||
            !TryDecodeBool(fields[10], out var isIdempotent))
        {
            return false;
        }

        model = new HttpEndpointEmission.Model(
            fields[0]!,
            fields[1]!,
            fields[2]!,
            fields[3]!,
            fields[4]!,
            fields[5]!,
            useAsParameters,
            disableAntiforgery,
            responseIsEmpty,
            fields[9],
            isIdempotent);
        return true;
    }

    public static bool TryDecodeRpcMethod(string value, out RpcMethodEmission.Model? model)
    {
        model = null;
        // 11 fields = an assembly built before the Connection transport flag existed; decode it with
        // onConnection = false (that compilation never opted into the connection surface) instead of
        // silently dropping its handlers from every transport.
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count is not (11 or 12))
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null || fields[3] is null ||
            fields[8] is null || fields[9] is null || fields[10] is null)
        {
            return false;
        }

        var onConnection = false;
        if (!TryDecodeBool(fields[5], out var onJsonRpc) ||
            !TryDecodeBool(fields[6], out var onMcp) ||
            !ElarionManifestCodec.TryDecodeParameters(fields[8]!, out var parameters) ||
            !TryDecodeBool(fields[9], out var isNameInferred) ||
            !TryDecodeBool(fields[10], out var isIdempotent) ||
            (fields.Count == 12 && !TryDecodeBool(fields[11], out onConnection)))
        {
            return false;
        }

        model = new RpcMethodEmission.Model(
            fields[0]!,
            fields[1]!,
            fields[2]!,
            fields[3]!,
            fields[4],
            onJsonRpc,
            onMcp,
            onConnection,
            fields[7],
            parameters.ToEquatableArray(),
            isNameInferred,
            isIdempotent);
        return true;
    }

    private static string EncodeBool(bool value) => value ? "1" : "0";

    private static bool TryDecodeBool(string? value, out bool result)
    {
        switch (value)
        {
            case "1":
                result = true;
                return true;
            case "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}

internal static class ElarionManifestCodec
{
    public static string EncodeFields(params string?[] fields)
    {
        var result = new System.Text.StringBuilder();
        foreach (var field in fields)
        {
            if (field is null)
            {
                result.Append("-1:");
                continue;
            }

            result.Append(field.Length);
            result.Append(':');
            result.Append(field);
        }

        return result.ToString();
    }

    public static bool TryDecodeFields(string value, out IReadOnlyList<string?> fields)
    {
        var result = new List<string?>();
        var index = 0;
        while (index < value.Length)
        {
            var lengthStart = index;
            if (value[index] == '-')
                index++;

            while (index < value.Length && char.IsDigit(value[index]))
                index++;

            if (index == lengthStart || index >= value.Length || value[index] != ':')
            {
                fields = [];
                return false;
            }

            if (!int.TryParse(value.Substring(lengthStart, index - lengthStart), out var length))
            {
                fields = [];
                return false;
            }

            index++;
            if (length == -1)
            {
                result.Add(null);
                continue;
            }

            if (length < 0 || index + length > value.Length)
            {
                fields = [];
                return false;
            }

            result.Add(value.Substring(index, length));
            index += length;
        }

        fields = result;
        return true;
    }

    public static string EncodeParameters(IReadOnlyList<RpcMethodEmission.ParameterDescription> parameters)
    {
        var fields = new string?[parameters.Count * 2];
        for (var i = 0; i < parameters.Count; i++)
        {
            fields[i * 2] = parameters[i].PropertyName;
            fields[i * 2 + 1] = parameters[i].Description;
        }

        return EncodeFields(fields);
    }

    public static bool TryDecodeParameters(string value, out IReadOnlyList<RpcMethodEmission.ParameterDescription> parameters)
    {
        parameters = [];
        if (!TryDecodeFields(value, out var fields) || fields.Count % 2 != 0)
            return false;

        var result = new List<RpcMethodEmission.ParameterDescription>();
        for (var i = 0; i < fields.Count; i += 2)
        {
            if (fields[i] is null || fields[i + 1] is null)
                return false;

            result.Add(new RpcMethodEmission.ParameterDescription(fields[i]!, fields[i + 1]!));
        }

        parameters = result;
        return true;
    }
}

/// <summary>
/// The outcome of reading one referenced assembly's Elarion manifest: the decoded <see cref="ElarionManifest.Data"/>
/// plus an optional <see cref="DiagnosticInfo"/> when the assembly advertises an unsupported schema version (in
/// which case <see cref="Data"/> is <see cref="ElarionManifest.Data.Empty"/>, so no entry is misparsed). The
/// diagnostic is carried as data — the consuming generator reports it — keeping the reader a pure transform.
/// </summary>
internal readonly record struct ManifestReadResult(ElarionManifest.Data Data, DiagnosticInfo? Diagnostic);

internal static class ElarionManifestReader
{
    public static ManifestReadResult Read(MetadataReference reference, CancellationToken ct)
    {
        var modules = new List<ElarionManifest.Module>();
        var moduleEndpointHooks = new List<ElarionManifest.ModuleEndpoints>();
        var httpEndpoints = new List<HttpEndpointEmission.Model>();
        var rpcMethods = new List<RpcMethodEmission.Model>();
        var resourceFilters = new List<ElarionManifest.ResourceFilter>();
        var permissions = new List<ElarionManifest.Permission>();
        var roles = new List<ElarionManifest.Role>();
        var variants = new List<ElarionManifest.Variant>();

        var entries = AssemblyMetadataReader.ReadRawEntries(reference, ct);

        string? schemaVersion = null;
        var hasElarionEntries = false;
        foreach (var (key, value) in entries)
        {
            if (key == ElarionManifest.SchemaKey)
            {
                schemaVersion = value;
                continue;
            }

            if (IsElarionManifestKey(key))
                hasElarionEntries = true;
        }

        // A version we do not understand (or Elarion entries with no advertised version at all) means a format we
        // may misparse. Skip the whole assembly's manifest and surface it loudly, since silently dropping
        // permission/role entries would weaken authorization.
        if ((schemaVersion is not null && schemaVersion != ElarionManifest.SchemaVersion)
            || (schemaVersion is null && hasElarionEntries))
        {
            var diagnostic = DiagnosticInfo.Create(
                ElarionManifest.UnsupportedManifestSchema,
                LocationInfo.From((Location?)null),
                DescribeReference(reference),
                schemaVersion ?? "<none>",
                ElarionManifest.SchemaVersion);
            return new ManifestReadResult(ElarionManifest.Data.Empty, diagnostic);
        }

        foreach (var (key, value) in entries)
            AddEntry(
                key, value, modules, moduleEndpointHooks, httpEndpoints, rpcMethods, resourceFilters, permissions,
                roles, variants);

        return new ManifestReadResult(
            CreateData(
                modules, moduleEndpointHooks, httpEndpoints, rpcMethods, resourceFilters, permissions, roles,
                variants),
            null);
    }

    private static bool IsElarionManifestKey(string key) =>
        key is ElarionManifest.ModuleKey
            or ElarionManifest.ModuleEndpointsKey
            or ElarionManifest.HttpEndpointKey
            or ElarionManifest.RpcMethodKey
            or ElarionManifest.ResourceFilterKey
            or ElarionManifest.PermissionKey
            or ElarionManifest.RoleKey
            or ElarionManifest.VariantKey;

    private static string DescribeReference(MetadataReference reference)
    {
        if (reference is CompilationReference compilationReference)
            return compilationReference.Compilation.AssemblyName ?? "<unknown>";

        return string.IsNullOrEmpty(reference.Display) ? "<unknown>" : reference.Display!;
    }

    private static ElarionManifest.Data CreateData(
        List<ElarionManifest.Module> modules,
        List<ElarionManifest.ModuleEndpoints> moduleEndpointHooks,
        List<HttpEndpointEmission.Model> httpEndpoints,
        List<RpcMethodEmission.Model> rpcMethods,
        List<ElarionManifest.ResourceFilter> resourceFilters,
        List<ElarionManifest.Permission> permissions,
        List<ElarionManifest.Role> roles,
        List<ElarionManifest.Variant> variants) =>
        ElarionManifest.Data.Combine(
            [
                new ElarionManifest.Data(
                    modules, moduleEndpointHooks, httpEndpoints, rpcMethods, resourceFilters, permissions, roles,
                    variants),
            ]);

    private static void AddEntry(
        string key,
        string value,
        List<ElarionManifest.Module> modules,
        List<ElarionManifest.ModuleEndpoints> moduleEndpointHooks,
        List<HttpEndpointEmission.Model> httpEndpoints,
        List<RpcMethodEmission.Model> rpcMethods,
        List<ElarionManifest.ResourceFilter> resourceFilters,
        List<ElarionManifest.Permission> permissions,
        List<ElarionManifest.Role> roles,
        List<ElarionManifest.Variant> variants)
    {
        switch (key)
        {
            case ElarionManifest.SchemaKey:
                break;
            case ElarionManifest.ModuleKey:
                if (ElarionManifest.TryDecodeModule(value, out var moduleEntry) && moduleEntry is not null)
                    modules.Add(moduleEntry);
                break;
            case ElarionManifest.ModuleEndpointsKey:
                if (ElarionManifest.TryDecodeModuleEndpoints(value, out var moduleEndpointsEntry)
                    && moduleEndpointsEntry is not null)
                {
                    moduleEndpointHooks.Add(moduleEndpointsEntry);
                }

                break;
            case ElarionManifest.HttpEndpointKey:
                if (ElarionManifest.TryDecodeHttpEndpoint(value, out var httpEndpoint) && httpEndpoint is not null)
                    httpEndpoints.Add(httpEndpoint);
                break;
            case ElarionManifest.RpcMethodKey:
                if (ElarionManifest.TryDecodeRpcMethod(value, out var rpcMethod) && rpcMethod is not null)
                    rpcMethods.Add(rpcMethod);
                break;
            case ElarionManifest.ResourceFilterKey:
                if (ElarionManifest.TryDecodeResourceFilter(value, out var resourceFilter) && resourceFilter is not null)
                    resourceFilters.Add(resourceFilter);
                break;
            case ElarionManifest.PermissionKey:
                if (ElarionManifest.TryDecodePermission(value, out var permission) && permission is not null)
                    permissions.Add(permission);
                break;
            case ElarionManifest.RoleKey:
                if (ElarionManifest.TryDecodeRole(value, out var role) && role is not null)
                    roles.Add(role);
                break;
            case ElarionManifest.VariantKey:
                if (ElarionManifest.TryDecodeVariant(value, out var variant) && variant is not null)
                    variants.Add(variant);
                break;
        }
    }

}
