using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// The audit slots in the handler registration pipeline (ADR-0045): attachment on [Auditable] and
/// [ElarionAuditDefaults], the compile-resolved action name, and the soft IAuditTrail gate.
/// </summary>
public sealed class AuditPipelineGeneratorTests {
    [Fact]
    public void AuditableHandler_EmitsBothAuditDecorators_SoftGatedOnTheTrail() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "",
            handlerAttributes: "[Elarion.Abstractions.Auditing.Auditable]",
            requestMarker: ": Elarion.Abstractions.ICommand"));

        generated.Should().Contain("if (sp.GetService<global::Elarion.Abstractions.Auditing.IAuditTrail>() is { } __auditCommitTrail)");
        generated.Should().Contain("global::Elarion.Pipeline.AuditCommitDecorator<");
        generated.Should().Contain("if (sp.GetService<global::Elarion.Abstractions.Auditing.IAuditTrail>() is { } __auditTrail)");
        generated.Should().Contain("global::Elarion.Pipeline.AuditDecorator<");
        generated.Should().Contain("sp.GetRequiredService<global::Elarion.Auditing.AuditScope>()");
        // The action is resolved like the RPC wire name: camel-cased module + inferred operation.
        generated.Should().Contain("\"sales.createOrder\"");
        generated.Should().Contain("\"Sales\"");
        // The outer decorator reads [Auditable] via the metadata singleton at run time.
        generated.Should().Contain("__handlerMetadata");
    }

    [Fact]
    public void ExplicitHandlerName_IsTheAuditAction() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "",
            handlerAttributes: """
                [Elarion.Abstractions.Handler("custom.name")]
                [Elarion.Abstractions.Auditing.Auditable]
                """,
            requestMarker: ": Elarion.Abstractions.ICommand"));

        generated.Should().Contain("\"custom.name\"");
    }

    [Fact]
    public void AuditDefaults_AttachToCommands_WithoutAnAttribute() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "[assembly: Elarion.Abstractions.Auditing.ElarionAuditDefaults]",
            handlerAttributes: "",
            requestMarker: ": Elarion.Abstractions.ICommand"));

        generated.Should().Contain("AuditCommitDecorator<");
        generated.Should().Contain("AuditDecorator<");
    }

    [Fact]
    public void AuditDefaults_DoNotAttachToQueries() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "[assembly: Elarion.Abstractions.Auditing.ElarionAuditDefaults]",
            handlerAttributes: "",
            requestMarker: ""));

        generated.Should().NotContain("AuditCommitDecorator<");
        generated.Should().NotContain("AuditDecorator<");
    }

    [Fact]
    public void AuditableEnabledFalse_OptsOutUnderDefaults() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "[assembly: Elarion.Abstractions.Auditing.ElarionAuditDefaults]",
            handlerAttributes: "[Elarion.Abstractions.Auditing.Auditable(Enabled = false)]",
            requestMarker: ": Elarion.Abstractions.ICommand"));

        generated.Should().NotContain("AuditCommitDecorator<");
        generated.Should().NotContain("AuditDecorator<");
    }

    [Fact]
    public void NoAuditableNoDefaults_EmitsNoAuditDecorators() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "",
            handlerAttributes: "",
            requestMarker: ": Elarion.Abstractions.ICommand"));

        generated.Should().NotContain("AuditCommitDecorator<");
        generated.Should().NotContain("AuditDecorator<");
    }

    [Fact]
    public void EmitsResolvedPipelineCache_WithConditionalFlags() {
        var generated = Generate(CreateSource(
            assemblyAttributes: "",
            handlerAttributes: "[Elarion.Abstractions.Auditing.Auditable]",
            requestMarker: ": Elarion.Abstractions.ICommand"));

        // The per-handler cache + first-resolution collector + publish.
        generated.Should().Contain(
            "private static volatile global::System.Collections.Generic.IReadOnlyList<global::Elarion.Abstractions.Pipeline.PipelineStep>? __pipeline;");
        generated.Should().Contain("var __steps = __pipeline is null");
        generated.Should().Contain("if (__steps is not null) { __steps.Reverse(); __pipeline = __steps; }");

        // Soft-attached audit is a conditional step; always-on tracing is not.
        generated.Should().Contain(
            "__steps?.Add(new global::Elarion.Abstractions.Pipeline.PipelineStep(typeof(global::Elarion.Pipeline.AuditDecorator<,>), true));");
        generated.Should().Contain(
            "__steps?.Add(new global::Elarion.Abstractions.Pipeline.PipelineStep(typeof(global::Elarion.Pipeline.TracingDecorator<,>), false));");
        // The metadata singleton feeds the accessor into HandlerMetadata.
        generated.Should().Contain(
            "static () => __pipeline ?? global::System.Array.Empty<global::Elarion.Abstractions.Pipeline.PipelineStep>());");
    }

    [Fact]
    public void IrrelevantEdit_ReusesHandlerOutputs() {
        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new HandlerRegistrationGenerator(),
            CreateSource(
                assemblyAttributes: "",
                handlerAttributes: "[Elarion.Abstractions.Auditing.Auditable]",
                requestMarker: ": Elarion.Abstractions.ICommand"),
            "Handlers");
    }

    private static string Generate(string source) {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "AuditPipelineGeneratorTests",
            [syntaxTree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        var result = driver.RunGenerators(compilation).GetRunResult();

        result.Diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return result.GeneratedTrees
            .Single(tree => Path.GetFileName(tree.FilePath) == "Sample_Modules_Sales_Handlers_CreateOrder.g.cs")
            .GetText()
            .ToString();
    }

    // A hermetic stub world: the generator matches attribute/interface METADATA NAMES, so source-declared
    // stand-ins keep the test independent of the real Abstractions surface (the established pattern of
    // HandlerRegistrationGeneratorTests).
    private static string CreateSource(string assemblyAttributes, string handlerAttributes, string requestMarker) =>
        $$"""
        {{assemblyAttributes}}

        namespace Elarion.Abstractions {
            public interface IHandler<TRequest, TResponse> {
                System.Threading.Tasks.ValueTask<TResponse> HandleAsync(
                    TRequest request,
                    System.Threading.CancellationToken ct);
            }

            public interface ICommand;

            public readonly record struct Result<T>(T Value);

            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class HandlerAttribute : System.Attribute {
                public HandlerAttribute() { }
                public HandlerAttribute(string name) { }
            }
        }

        namespace Elarion.Abstractions.Auditing {
            [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class AuditableAttribute : System.Attribute {
                public bool Enabled { get; init; } = true;
                public string? Resource { get; init; }
            }

            [System.AttributeUsage(System.AttributeTargets.Assembly | System.AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
            public sealed class ElarionAuditDefaultsAttribute : System.Attribute;
        }

        namespace Elarion.Abstractions.Modules {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class AppModuleAttribute(string name) : System.Attribute {
                public string Name { get; } = name;
            }
        }

        namespace Sample.Modules.Sales {
            [Elarion.Abstractions.Modules.AppModule("Sales")]
            public static partial class SalesModule;
        }

        namespace Sample.Modules.Sales.Handlers {
            {{handlerAttributes}}
            public sealed class CreateOrder
                : Elarion.Abstractions.IHandler<CreateOrder.Command, Elarion.Abstractions.Result<CreateOrder.Response>> {
                public sealed record Command {{requestMarker}} {
                    public required string CustomerId { get; init; }
                }

                public sealed record Response;

                public System.Threading.Tasks.ValueTask<Elarion.Abstractions.Result<Response>> HandleAsync(
                    Command request,
                    System.Threading.CancellationToken ct) =>
                    default;
            }
        }
        """;

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");

        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
