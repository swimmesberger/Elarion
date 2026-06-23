using Microsoft.CodeAnalysis;

namespace Elarion.Generators;

internal static class ElarionManifest
{
    public const string SchemaKey = "Elarion.Manifest.Schema";
    public const string SchemaVersion = "1";
    public const string ModuleKey = "Elarion.Manifest.Module.v1";
    public const string HttpEndpointKey = "Elarion.Manifest.HttpEndpoint.v1";
    public const string RpcMethodKey = "Elarion.Manifest.RpcMethod.v1";

    public sealed record Module(
        string ModuleName,
        string Namespace,
        string TypeFqn,
        string? DependsOn,
        bool IsCore,
        bool HasConfigureServices,
        bool HasMapEndpoints,
        bool HasGetJsonTypeInfoResolver,
        bool HasConfigureEndpointGroup
    );

    public sealed record Data(
        IReadOnlyList<Module> Modules,
        IReadOnlyList<HttpEndpointEmission.Model> HttpEndpoints,
        IReadOnlyList<RpcMethodEmission.Model> RpcMethods
    )
    {
        public static readonly Data Empty = new([], [], []);

        public bool HasEntries => Modules.Count > 0 || HttpEndpoints.Count > 0 || RpcMethods.Count > 0;

        public static Data Combine(IEnumerable<Data> manifests)
        {
            var modules = new List<Module>();
            var httpEndpoints = new List<HttpEndpointEmission.Model>();
            var rpcMethods = new List<RpcMethodEmission.Model>();

            foreach (var manifest in manifests)
            {
                modules.AddRange(manifest.Modules);
                httpEndpoints.AddRange(manifest.HttpEndpoints);
                rpcMethods.AddRange(manifest.RpcMethods);
            }

            modules.Sort(static (a, b) =>
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

            return new Data(modules, httpEndpoints, rpcMethods);
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
            EncodeBool(module.HasConfigureEndpointGroup));

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
            model.Description);

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
            ElarionManifestCodec.EncodeParameters(model.Parameters));

    public static bool TryDecodeModule(string value, out Module? module)
    {
        module = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 9)
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

        module = new Module(
            fields[0]!,
            fields[1]!,
            fields[2]!,
            fields[3],
            isCore,
            hasConfigureServices,
            hasMapEndpoints,
            hasGetJsonTypeInfoResolver,
            hasConfigureEndpointGroup);
        return true;
    }

    public static bool TryDecodeHttpEndpoint(string value, out HttpEndpointEmission.Model? model)
    {
        model = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 10)
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null || fields[3] is null ||
            fields[4] is null || fields[5] is null)
        {
            return false;
        }

        if (!TryDecodeBool(fields[6], out var useAsParameters) ||
            !TryDecodeBool(fields[7], out var disableAntiforgery) ||
            !TryDecodeBool(fields[8], out var responseIsEmpty))
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
            fields[9]);
        return true;
    }

    public static bool TryDecodeRpcMethod(string value, out RpcMethodEmission.Model? model)
    {
        model = null;
        if (!ElarionManifestCodec.TryDecodeFields(value, out var fields) || fields.Count != 9)
            return false;
        if (fields[0] is null || fields[1] is null || fields[2] is null || fields[3] is null ||
            fields[8] is null)
        {
            return false;
        }

        if (!TryDecodeBool(fields[5], out var onJsonRpc) ||
            !TryDecodeBool(fields[6], out var onMcp) ||
            !ElarionManifestCodec.TryDecodeParameters(fields[8]!, out var parameters))
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
            fields[7],
            parameters.ToEquatableArray());
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

internal static class ElarionManifestReader
{
    public static ElarionManifest.Data Read(MetadataReference reference, CancellationToken ct)
    {
        var modules = new List<ElarionManifest.Module>();
        var httpEndpoints = new List<HttpEndpointEmission.Model>();
        var rpcMethods = new List<RpcMethodEmission.Model>();

        foreach (var (key, value) in AssemblyMetadataReader.ReadRawEntries(reference, ct))
            AddEntry(key, value, modules, httpEndpoints, rpcMethods);

        return CreateData(modules, httpEndpoints, rpcMethods);
    }

    private static ElarionManifest.Data CreateData(
        List<ElarionManifest.Module> modules,
        List<HttpEndpointEmission.Model> httpEndpoints,
        List<RpcMethodEmission.Model> rpcMethods) =>
        ElarionManifest.Data.Combine([new ElarionManifest.Data(modules, httpEndpoints, rpcMethods)]);

    private static void AddEntry(
        string key,
        string value,
        List<ElarionManifest.Module> modules,
        List<HttpEndpointEmission.Model> httpEndpoints,
        List<RpcMethodEmission.Model> rpcMethods)
    {
        switch (key)
        {
            case ElarionManifest.SchemaKey:
                break;
            case ElarionManifest.ModuleKey:
                if (ElarionManifest.TryDecodeModule(value, out var moduleEntry) && moduleEntry is not null)
                    modules.Add(moduleEntry);
                break;
            case ElarionManifest.HttpEndpointKey:
                if (ElarionManifest.TryDecodeHttpEndpoint(value, out var httpEndpoint) && httpEndpoint is not null)
                    httpEndpoints.Add(httpEndpoint);
                break;
            case ElarionManifest.RpcMethodKey:
                if (ElarionManifest.TryDecodeRpcMethod(value, out var rpcMethod) && rpcMethod is not null)
                    rpcMethods.Add(rpcMethod);
                break;
        }
    }

}
