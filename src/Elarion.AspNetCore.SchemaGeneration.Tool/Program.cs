using System.Text;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.ClientEvents;
using Elarion.Abstractions.Modules;
using Elarion.JsonRpc;

namespace Elarion.AspNetCore.SchemaGeneration.Tool;

internal static class Program {
    public static async Task<int> Main(string[] args) {
        try {
            var options = CommandLineOptions.Parse(args);
            if (options.ShowHelp) {
                Console.WriteLine(CommandLineOptions.HelpText);
                return 0;
            }

            await GenerateAsync(options);
            return 0;
        }
        catch (CommandLineException ex) {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(CommandLineOptions.HelpText);
            return 2;
        }
        catch (Exception ex) {
            Console.Error.WriteLine($"error ELARIONRPCGEN: {ex.Message}");
            return 1;
        }
    }

    private static async Task GenerateAsync(CommandLineOptions options) {
        var assemblyPath = Path.GetFullPath(options.AssemblyPath);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var fileListPath = options.FileListPath is null ? null : Path.GetFullPath(options.FileListPath);

        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException($"Application assembly '{assemblyPath}' does not exist.", assemblyPath);

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new InvalidOperationException($"Schema output path '{outputPath}' does not include a directory.");

        Directory.CreateDirectory(outputDirectory);

        using var environment = SchemaGenerationEnvironment.Apply(options.EnvironmentName, outputPath);
        using var loader = new ApplicationHostLoader(assemblyPath);
        using var host = await loader.LoadAsync(options.ApplicationArguments);

        var dispatcher = host.Services.GetService(typeof(JsonRpcDispatcher)) as JsonRpcDispatcher
                         ?? throw new InvalidOperationException(
                             "The application did not register a JsonRpcDispatcher service. Register the dispatcher before building the host.");

        // Capability vocabulary (ADR-0032): both optional, resolved from the app's own registrations — the
        // manifest when a module opts in via [ClientFeatures] + AddElarionSession, the catalog when the host
        // calls AddElarionAuthorization. Absent, the schema is byte-identical to a vocabulary-free export.
        var exportOptions = new JsonRpcSchemaExportOptions {
            ClientCapabilities = host.Services.GetService(typeof(ClientCapabilityManifest)) as ClientCapabilityManifest,
            PermissionCatalog = host.Services.GetService(typeof(IPermissionCatalog)) as IPermissionCatalog,
            // Present when the host calls AddElarionClientEvents; absent, the schema carries no events block.
            ClientEventTopics = host.Services.GetService(typeof(ClientEventTopicManifest)) as ClientEventTopicManifest
        };

        var schemaJson = JsonRpcSchemaExporter.Generate(dispatcher, dispatcher.JsonOptions, exportOptions);

        await File.WriteAllTextAsync(outputPath, schemaJson, new UTF8Encoding(false));

        if (fileListPath is not null) {
            var fileListDirectory = Path.GetDirectoryName(fileListPath);
            if (!string.IsNullOrWhiteSpace(fileListDirectory)) Directory.CreateDirectory(fileListDirectory);

            await File.WriteAllTextAsync(fileListPath, outputPath + Environment.NewLine, Encoding.UTF8);
        }

        Console.WriteLine($"Elarion JSON-RPC schema written to {outputPath} ({dispatcher.MethodNames.Count} methods)");
    }
}
