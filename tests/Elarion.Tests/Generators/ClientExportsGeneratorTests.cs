using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// <c>[ClientConstants]</c> classes register their exportable members per module; <c>[RequestPreset]</c> fields are
/// checked to be static readonly request values of a JSON-RPC handler (ADR-0100).
/// </summary>
public sealed class ClientExportsGeneratorTests {
    private const string Preamble =
        """
        using System;
        using System.Collections.Generic;
        using System.Collections.Immutable;
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;

        [assembly: UseElarion]
        """;

    private const string ConstantsFile = "Sample_App_AppModuleElarionModuleServices.ClientConstants.g.cs";

    [Fact]
    public void RegistersTheExportableMembersOfAConstantsClass_InItsModule() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public enum Currency { Eur, Chf }

                                  [ClientConstants]
                                  internal static class BillingLimits {
                                      public const decimal MaxTransferAmount = 999_999.99m;
                                      public const int MaxReceipts = 10;
                                      public static readonly IReadOnlyList<string> Formats = ["camt.053", "mt940"];
                                      public static readonly Currency[] Currencies = [Currency.Eur, Currency.Chf];
                                      public static ImmutableArray<int> Sizes { get; } = [10, 20];
                                      internal const int NotExported = 1;
                                      private static readonly object Helper = new();
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);
        var generated = GetGenerated(result, ConstantsFile);

        diagnostics.Should().BeEmpty();
        generated.Should().Contain("static partial void AddClientConstants(");
        generated.Should().Contain("Module = \"App\",");
        generated.Should().Contain(
            "new() { Name = \"maxTransferAmount\", Type = typeof(decimal), GetValue = static () => global::Sample.App.BillingLimits.MaxTransferAmount },");
        generated.Should().Contain(
            "new() { Name = \"formats\", Type = typeof(global::System.Collections.Generic.IReadOnlyList<string>), GetValue = static () => global::Sample.App.BillingLimits.Formats },");
        generated.Should().Contain("Name = \"currencies\", Type = typeof(global::Sample.App.Currency[])");
        generated.Should().Contain("Name = \"sizes\"");
        generated.Should().NotContain("NotExported").And.NotContain("Helper");

        AssertCompiles(source, result);
    }

    [Fact]
    public void ReportsUnexportableMembers_AndExportsTheRest() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  [ClientConstants]
                                  public static class Limits {
                                      public const int Max = 3;
                                      public static readonly DateTime Since = DateTime.UnixEpoch;
                                      public static int Counter = 0;
                                      public static string Name { get; set; } = "x";
                                  }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().Equal("ELRPC010", "ELRPC010", "ELRPC010");
        diagnostics.Select(static d => d.GetMessage()).Should().Contain(m => m.Contains("'Since'") && m.Contains("not exportable"))
            .And.Contain(m => m.Contains("'Counter'") && m.Contains("mutable"))
            .And.Contain(m => m.Contains("'Name'") && m.Contains("setter"));
        GetGenerated(result, ConstantsFile).Should().Contain("Name = \"max\"").And.NotContain("Since");
    }

    [Fact]
    public void ReportsAConstantsClassItsModuleCannotRegister() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  [ClientConstants]
                                  public class NotStatic { public const int A = 1; }

                                  public static class Outer {
                                      [ClientConstants]
                                      private static class Hidden { public const int B = 1; }
                                  }
                              }

                              namespace Elsewhere {
                                  [ClientConstants]
                                  public static class Orphan { public const int C = 1; }
                              }
                              """;

        var (result, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().OnlyContain(static id => id == "ELRPC009").And.HaveCount(3);
        diagnostics.Select(static d => d.GetMessage()).Should().Contain(m => m.Contains("not a static class"))
            .And.Contain(m => m.Contains("'Hidden' is not accessible"))
            .And.Contain(m => m.Contains("not under any [AppModule]"));
        result.GeneratedTrees.Should().NotContain(t => t.FilePath.EndsWith(ConstantsFile, StringComparison.Ordinal));
    }

    [Fact]
    public void ReportsTwoConstantsOfOneModuleWithTheSameExportedName() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  [ClientConstants]
                                  public static class First { public const int MaxItems = 1; }

                                  [ClientConstants]
                                  public static class Second { public const int maxItems = 2; }
                              }
                              """;

        var (_, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().Equal("ELRPC011");
        diagnostics[0].GetMessage().Should().Contain("'maxItems'");
    }

    private const string Handler =
        """

        namespace Sample.App {
            [AppModule("App")]
            public static class AppModule { }

            public sealed record ListQuery(bool? ToTransfer = null, int Size = 20) : IQuery;
            public sealed record OtherQuery(int Id) : IQuery;

            [Handler]
            public sealed class ListThings : IHandler<ListQuery, Result<int>> {
                {{FIELDS}}

                public ValueTask<Result<int>> HandleAsync(ListQuery request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<int>>(1);
            }
        }
        """;

    [Fact]
    public void AcceptsAPresetOfAJsonRpcHandler() {
        var (_, diagnostics) = Run(Preamble + Handler.Replace("{{FIELDS}}", """
            [RequestPreset("toTransfer")]
            public static readonly ListQuery ToTransfer = new(ToTransfer: true);

            [RequestPreset("big")]
            private static readonly ListQuery Big = new(Size: 100);
            """, StringComparison.Ordinal));

        diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("[RequestPreset(\"a\")] public static ListQuery A = new();", "not static readonly")]
    [InlineData("[RequestPreset(\"a\")] public readonly ListQuery A = new();", "not static readonly")]
    [InlineData("[RequestPreset(\"a\")] public static readonly OtherQuery A = new(1);", "is not the handler's request type")]
    [InlineData("[RequestPreset(\" \")] public static readonly ListQuery A = new();", "its name is blank")]
    public void ReportsAnInvalidPreset(string field, string reason) {
        var (_, diagnostics) = Run(Preamble + Handler.Replace("{{FIELDS}}", field, StringComparison.Ordinal));

        diagnostics.Select(static d => d.Id).Should().Equal("ELRPC006");
        diagnostics[0].GetMessage().Should().Contain(reason);
    }

    [Fact]
    public void ReportsAPresetOutsideAHandler_ADuplicateName_AndAHandlerOffJsonRpc() {
        const string source = Preamble +
                              """

                              namespace Sample.App {
                                  [AppModule("App")]
                                  public static class AppModule { }

                                  public sealed record ListQuery(bool? ToTransfer = null) : IQuery;

                                  public static class Presets {
                                      [RequestPreset("a")] public static readonly ListQuery A = new();
                                  }

                                  [Handler]
                                  public sealed class ListThings : IHandler<ListQuery, Result<int>> {
                                      [RequestPreset("same")] public static readonly ListQuery First = new();
                                      [RequestPreset("same")] public static readonly ListQuery Second = new(true);

                                      public ValueTask<Result<int>> HandleAsync(ListQuery request, CancellationToken ct) =>
                                          ValueTask.FromResult<Result<int>>(1);
                                  }

                                  public sealed record McpQuery(int Id) : IQuery;

                                  [Handler(Transports = HandlerTransports.Mcp)]
                                  public sealed class McpOnly : IHandler<McpQuery, Result<int>> {
                                      [RequestPreset("one")] public static readonly McpQuery One = new(1);

                                      public ValueTask<Result<int>> HandleAsync(McpQuery request, CancellationToken ct) =>
                                          ValueTask.FromResult<Result<int>>(1);
                                  }
                              }
                              """;

        var (_, diagnostics) = Run(source);

        diagnostics.Select(static d => d.Id).Should().BeEquivalentTo(["ELRPC006", "ELRPC007", "ELRPC008"]);
        diagnostics.Single(static d => d.Id == "ELRPC006").GetMessage().Should().Contain("is not a handler");
        diagnostics.Single(static d => d.Id == "ELRPC007").GetMessage().Should().Contain("'same'");
    }

    [Fact]
    public void ReusesOutputsAfterIrrelevantEdit() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ClientExportsGenerator(),
            Preamble +
            """

            namespace Sample.App {
                [AppModule("App")]
                public static class AppModule { }

                [ClientConstants]
                public static class Limits { public const int Max = 3; }

                public sealed record ListQuery(bool? ToTransfer = null) : IQuery;

                [Handler]
                public sealed class ListThings : IHandler<ListQuery, Result<int>> {
                    [RequestPreset("a")] public static readonly ListQuery A = new(true);

                    public System.Threading.Tasks.ValueTask<Result<int>> HandleAsync(ListQuery request, System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.ValueTask.FromResult<Result<int>>(1);
                }
            }
            """,
            "ClientConstantsClasses",
            "RequestPresetFields",
            "ClientExportsCombined");
    }

    private static (GeneratorDriverRunResult Result, IReadOnlyList<Diagnostic> Diagnostics) Run(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ClientExportsGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new ClientExportsGenerator(), new ModuleDefaultServicesGenerator());
        var result = driver.RunGenerators(compilation, TestContext.Current.CancellationToken).GetRunResult();
        return (result, result.Diagnostics);
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    // The source with every generated file (the module skeleton and the constants registration) compiles.
    private static void AssertCompiles(string source, GeneratorDriverRunResult result) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "ClientExportsGeneratorCompile",
            [
                CSharpSyntaxTree.ParseText(source, parseOptions),
                .. result.GeneratedTrees.Select(tree => CSharpSyntaxTree.ParseText(tree.GetText(), parseOptions))
            ],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => SharedMetadataReferences.FromFile(path))
            .ToArray();
    }
}
