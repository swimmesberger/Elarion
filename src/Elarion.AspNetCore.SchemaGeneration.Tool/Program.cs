using System.Text;

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
        } catch (CommandLineException ex) {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(CommandLineOptions.HelpText);
            return 2;
        } catch (Exception ex) {
            Console.Error.WriteLine($"error ELARIONRPCGEN: {ex.Message}");
            return 1;
        }
    }

    private static async Task GenerateAsync(CommandLineOptions options) {
        var assemblyPath = Path.GetFullPath(options.AssemblyPath);
        var outputPath = Path.GetFullPath(options.OutputPath);
        var fileListPath = options.FileListPath is null ? null : Path.GetFullPath(options.FileListPath);

        if (!File.Exists(assemblyPath)) {
            throw new FileNotFoundException($"Application assembly '{assemblyPath}' does not exist.", assemblyPath);
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory)) {
            throw new InvalidOperationException($"Schema output path '{outputPath}' does not include a directory.");
        }

        Directory.CreateDirectory(outputDirectory);

        using var environment = SchemaGenerationEnvironment.Apply(options.EnvironmentName, outputPath);
        using var loader = new ApplicationHostLoader(assemblyPath);
        using var host = await loader.LoadAsync(options.ApplicationArguments);

        var dispatcher = host.Services.GetService(typeof(JsonRpcDispatcher)) as JsonRpcDispatcher
            ?? throw new InvalidOperationException(
                "The application did not register a JsonRpcDispatcher service. Register the dispatcher before building the host.");

        var schemaJson = JsonRpcSchemaExporter.Generate(dispatcher, dispatcher.JsonOptions);

        await File.WriteAllTextAsync(outputPath, schemaJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (fileListPath is not null) {
            var fileListDirectory = Path.GetDirectoryName(fileListPath);
            if (!string.IsNullOrWhiteSpace(fileListDirectory)) {
                Directory.CreateDirectory(fileListDirectory);
            }

            await File.WriteAllTextAsync(fileListPath, outputPath + Environment.NewLine, Encoding.UTF8);
        }

        Console.WriteLine($"Elarion JSON-RPC schema written to {outputPath} ({dispatcher.MethodNames.Count} methods)");
    }
}
