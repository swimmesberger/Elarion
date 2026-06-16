using System.Reflection;

namespace Elarion.AspNetCore;

/// <summary>
/// Helpers for detecting when an application is being launched by Elarion's build-time
/// JSON-RPC schema generation tool.
/// </summary>
public static class JsonRpcSchemaGeneration {
    /// <summary>
    /// Environment variable set to <c>true</c> while the build-time schema generation tool
    /// is loading the application.
    /// </summary>
    public const string IsRunningEnvironmentVariable = "ELARION_JSONRPC_SCHEMA_GENERATION";

    /// <summary>
    /// Environment variable containing the schema output path requested by the build tool.
    /// </summary>
    public const string OutputPathEnvironmentVariable = "ELARION_JSONRPC_SCHEMA_OUTPUT";

    /// <summary>
    /// Entry assembly name used by the build-time schema generation tool.
    /// </summary>
    public const string ToolEntryAssemblyName = "dotnet-elarion-getrpcschema";

    /// <summary>
    /// Returns <see langword="true"/> when the current process is running only to generate
    /// the JSON-RPC schema at build time.
    /// </summary>
    public static bool IsRunning =>
        string.Equals(
            Environment.GetEnvironmentVariable(IsRunningEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            Assembly.GetEntryAssembly()?.GetName().Name,
            ToolEntryAssemblyName,
            StringComparison.Ordinal);

    /// <summary>
    /// Gets the requested schema output path when schema generation is active.
    /// </summary>
    public static string? OutputPath => Environment.GetEnvironmentVariable(OutputPathEnvironmentVariable);
}
