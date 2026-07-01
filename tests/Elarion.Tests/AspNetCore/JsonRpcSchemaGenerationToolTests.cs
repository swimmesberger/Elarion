using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Xunit;

namespace Elarion.Tests.AspNetCore;

public sealed class JsonRpcSchemaGenerationToolTests {
    [Fact]
    public async Task IsRunning_DetectsSchemaGenerationToolEntryAssemblyName() {
        var root = FindRepositoryRoot();
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"elarion-schema-entry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDirectory);

        var project = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <AssemblyName>dotnet-elarion-getrpcschema</AssemblyName>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{Path.Combine(root, "src", "Elarion.JsonRpc", "Elarion.JsonRpc.csproj")}}" />
              </ItemGroup>
            </Project>
            """;
        var program = """
            using Elarion.JsonRpc;

            Console.WriteLine(JsonRpcSchemaGeneration.IsRunning);
            """;

        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "EntryNameProbe.csproj"),
            project,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, "Program.cs"),
            program,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);

        var result = await RunDotnetAsync(projectDirectory, "run", "--configuration", TestConfiguration, "--nologo");

        result.ExitCode.Should().Be(0, result.ToString());
        result.StandardOutput.Trim().Should().Be("True");
    }

    [Fact]
    public async Task Tool_ExportsSchemaFromBuiltApplicationHost() {
        var root = FindRepositoryRoot();
        var projectDirectory = await CreateFixtureApplicationAsync(root, package: null);
        var build = await RunDotnetAsync(projectDirectory, "build", "--configuration", TestConfiguration, "--nologo");
        build.ExitCode.Should().Be(0, build.ToString());

        var assemblyPath = Path.Combine(projectDirectory, "bin", TestConfiguration, "net10.0", "FixtureApp.dll");
        var schemaPath = Path.Combine(projectDirectory, "rpc-schema.json");
        var toolPath = GetToolPath(root);

        var result = await RunDotnetAsync(
            projectDirectory,
            toolPath,
            "--assembly",
            assemblyPath,
            "--output",
            schemaPath);

        result.ExitCode.Should().Be(0, result.ToString());
        File.ReadAllText(schemaPath).Should().Contain("\"sample.ping\"");
    }

    [Fact]
    public async Task BuildTarget_GeneratesSchemaDuringBuild() {
        var root = FindRepositoryRoot();
        var package = await PackSchemaGenerationPackageAsync(root);
        var projectDirectory = await CreateFixtureApplicationAsync(root, package);

        var result = await RunDotnetAsync(projectDirectory, "build", "--configuration", TestConfiguration, "--nologo");

        result.ExitCode.Should().Be(0, result.ToString());
        File.ReadAllText(Path.Combine(projectDirectory, "rpc-schema.json")).Should().Contain("\"sample.ping\"");
    }

    private static async Task<string> CreateFixtureApplicationAsync(string root, PackedPackage? package) {
        var projectDirectory = Path.Combine(Path.GetTempPath(), $"elarion-schema-fixture-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectDirectory);

        var generationProperties = package is not null
            ? $$"""
                <RestoreAdditionalProjectSources>{{package.SourceDirectory}}</RestoreAdditionalProjectSources>
                <ElarionJsonRpcGenerateSchema>true</ElarionJsonRpcGenerateSchema>
                <ElarionJsonRpcSchemaOutputPath>$(MSBuildProjectDirectory)/rpc-schema.json</ElarionJsonRpcSchemaOutputPath>
                """
            : "";
        var packageReference = package is not null
            ? $"""<PackageReference Include="Elarion.AspNetCore.SchemaGeneration" Version="{package.Version}" PrivateAssets="all" />"""
            : "";

        var project = $$"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                {{generationProperties}}
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{{Path.Combine(root, "src", "Elarion.AspNetCore", "Elarion.AspNetCore.csproj")}}" />
                <ProjectReference Include="{{Path.Combine(root, "src", "Elarion.JsonRpc", "Elarion.JsonRpc.csproj")}}" />
                {{packageReference}}
              </ItemGroup>
            </Project>
            """;

        var program = """
            using System.Text.Json;
            using Elarion.Abstractions;
            using Elarion.AspNetCore;
            using Elarion.JsonRpc;

            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var builder = WebApplication.CreateSlimBuilder(args);
            builder.Services.AddElarionJsonRpc();

            var dispatcher = new JsonRpcDispatcher(options)
                .MapDelegate<PingRequest, PingResponse>(
                    "sample.ping",
                    (request, _, _) => ValueTask.FromResult<Result<PingResponse>>(new PingResponse(request.Message)))
                .Freeze();

            builder.Services.AddSingleton(dispatcher);

            var app = builder.Build();

            if (JsonRpcSchemaGeneration.IsRunning) {
                throw new InvalidOperationException("Code after Build should not run during schema generation.");
            }

            app.MapElarionJsonRpc();
            await app.RunAsync();

            public sealed record PingRequest(string Message);
            public sealed record PingResponse(string Message);
            """;

        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "FixtureApp.csproj"), project, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(projectDirectory, "Program.cs"), program, Encoding.UTF8);

        return projectDirectory;
    }

    private static string GetToolPath(string root) =>
        Path.Combine(
            root,
            "src",
            "Elarion.AspNetCore.SchemaGeneration.Tool",
            "bin",
            TestConfiguration,
            "net10.0",
            "dotnet-elarion-getrpcschema.dll");

    private static async Task<PackedPackage> PackSchemaGenerationPackageAsync(string root) {
        var packageSource = Path.Combine(Path.GetTempPath(), $"elarion-schema-packages-{Guid.NewGuid():N}");
        var version = $"0.1.0-schema-test.{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        Directory.CreateDirectory(packageSource);

        var result = await RunDotnetAsync(
            root,
            "pack",
            Path.Combine(root, "src", "Elarion.AspNetCore.SchemaGeneration", "Elarion.AspNetCore.SchemaGeneration.csproj"),
            "--configuration",
            TestConfiguration,
            "--no-build",
            "--output",
            packageSource,
            $"-p:PackageVersion={version}",
            "--nologo");

        result.ExitCode.Should().Be(0, result.ToString());
        Directory.GetFiles(packageSource, $"Elarion.AspNetCore.SchemaGeneration.{version}.nupkg")
            .Should().ContainSingle(result.ToString());

        return new PackedPackage(packageSource, version);
    }

    private static async Task<CommandResult> RunDotnetAsync(string workingDirectory, params string[] arguments) {
        var startInfo = new ProcessStartInfo("dotnet") {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet process.");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CommandResult(process.ExitCode, stdout, stderr);
    }

    private static string FindRepositoryRoot() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) {
            if (File.Exists(Path.Combine(directory.FullName, "Elarion.slnx"))) {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }

    private static string TestConfiguration {
        get {
            var baseDirectory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
            return baseDirectory.Parent?.Name ?? "Debug";
        }
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError) {
        public override string ToString() =>
            $"""
            Exit code: {ExitCode}
            Standard output:
            {StandardOutput}
            Standard error:
            {StandardError}
            """;
    }

    private sealed record PackedPackage(string SourceDirectory, string Version);
}
