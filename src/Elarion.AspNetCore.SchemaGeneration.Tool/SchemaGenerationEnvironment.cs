using Elarion.JsonRpc;

namespace Elarion.AspNetCore.SchemaGeneration.Tool;

internal sealed class SchemaGenerationEnvironment : IDisposable {
    private const string DotnetEnvironmentVariable = "DOTNET_ENVIRONMENT";
    private const string AspNetCoreEnvironmentVariable = "ASPNETCORE_ENVIRONMENT";

    private readonly Dictionary<string, string?> _previousValues;

    private SchemaGenerationEnvironment(Dictionary<string, string?> previousValues) {
        _previousValues = previousValues;
    }

    public static SchemaGenerationEnvironment Apply(string environmentName, string outputPath) {
        var previousValues = new Dictionary<string, string?> {
            [DotnetEnvironmentVariable] = Environment.GetEnvironmentVariable(DotnetEnvironmentVariable),
            [AspNetCoreEnvironmentVariable] = Environment.GetEnvironmentVariable(AspNetCoreEnvironmentVariable),
            [JsonRpcSchemaGeneration.IsRunningEnvironmentVariable] = Environment.GetEnvironmentVariable(JsonRpcSchemaGeneration.IsRunningEnvironmentVariable),
            [JsonRpcSchemaGeneration.OutputPathEnvironmentVariable] = Environment.GetEnvironmentVariable(JsonRpcSchemaGeneration.OutputPathEnvironmentVariable),
        };

        Environment.SetEnvironmentVariable(DotnetEnvironmentVariable, environmentName);
        Environment.SetEnvironmentVariable(AspNetCoreEnvironmentVariable, environmentName);
        Environment.SetEnvironmentVariable(JsonRpcSchemaGeneration.IsRunningEnvironmentVariable, "true");
        Environment.SetEnvironmentVariable(JsonRpcSchemaGeneration.OutputPathEnvironmentVariable, outputPath);

        return new SchemaGenerationEnvironment(previousValues);
    }

    public void Dispose() {
        foreach (var (name, value) in _previousValues) {
            Environment.SetEnvironmentVariable(name, value);
        }
    }
}
