namespace Elarion.AspNetCore.SchemaGeneration.Tool;

internal sealed record CommandLineOptions(
    string AssemblyPath,
    string OutputPath,
    string? FileListPath,
    string? ProjectName,
    string? FrameworkName,
    string EnvironmentName,
    string[] ApplicationArguments,
    bool ShowHelp) {
    public const string HelpText = """
        Elarion JSON-RPC schema generation tool.

        Required:
          --assembly <path>       Path to the application assembly to load.
          --output <path>         Path where rpc-schema.json should be written.

        Optional:
          --file-list <path>      Path to write a newline-delimited list of generated files.
          --project <name>        Project name for diagnostics.
          --framework <name>      Target framework moniker for diagnostics.
          --environment <name>    DOTNET_ENVIRONMENT/ASPNETCORE_ENVIRONMENT value. Defaults to Development.
          --help                  Show help.

        Any arguments after -- are passed to the application entry point.
        """;

    public static CommandLineOptions Parse(string[] args) {
        string? assemblyPath = null;
        string? outputPath = null;
        string? fileListPath = null;
        string? projectName = null;
        string? frameworkName = null;
        var environmentName = "Development";
        var applicationArguments = Array.Empty<string>();

        for (var i = 0; i < args.Length; i++) {
            var arg = args[i];
            if (arg == "--") {
                applicationArguments = args[(i + 1)..];
                break;
            }

            if (arg is "--help" or "-h") {
                return new CommandLineOptions(
                    AssemblyPath: string.Empty,
                    OutputPath: string.Empty,
                    FileListPath: null,
                    ProjectName: null,
                    FrameworkName: null,
                    EnvironmentName: environmentName,
                    ApplicationArguments: applicationArguments,
                    ShowHelp: true);
            }

            var value = ReadValue(args, ref i, arg);
            switch (arg) {
                case "--assembly":
                    assemblyPath = value;
                    break;
                case "--output":
                    outputPath = value;
                    break;
                case "--file-list":
                    fileListPath = value;
                    break;
                case "--project":
                    projectName = value;
                    break;
                case "--framework":
                    frameworkName = value;
                    break;
                case "--environment":
                    environmentName = value;
                    break;
                default:
                    throw new CommandLineException($"Unknown option '{arg}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(assemblyPath)) {
            throw new CommandLineException("Missing required option '--assembly <path>'.");
        }

        if (string.IsNullOrWhiteSpace(outputPath)) {
            throw new CommandLineException("Missing required option '--output <path>'.");
        }

        return new CommandLineOptions(
            assemblyPath,
            outputPath,
            fileListPath,
            projectName,
            frameworkName,
            environmentName,
            applicationArguments,
            ShowHelp: false);
    }

    private static string ReadValue(string[] args, ref int index, string option) {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) {
            throw new CommandLineException($"Missing value for option '{option}'.");
        }

        index++;
        return args[index];
    }
}

internal sealed class CommandLineException(string message) : Exception(message);
