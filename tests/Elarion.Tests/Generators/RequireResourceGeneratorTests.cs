using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class RequireResourceGeneratorTests
{
    private const string Source =
        """
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;

        [assembly: Elarion.Abstractions.GenerateModuleHandlers]

        namespace Sample.App
        {
            [Elarion.Abstractions.Modules.AppModule("App")]
            public static partial class AppModule;

            public sealed class Contact { public System.Guid Id { get; set; } }

            [RequireResource(typeof(Contact), Operation = "read", Id = nameof(GetContact.Query.Id))]
            public sealed class GetContact : IHandler<GetContact.Query, Result<string>>
            {
                public sealed record Query { public System.Guid Id { get; init; } }

                public System.Threading.Tasks.ValueTask<Result<string>> HandleAsync(Query request, System.Threading.CancellationToken ct)
                    => default;
            }
        }
        """;

    [Fact]
    public void RequireResource_EmitsTypedBindingAndPassesToDecorator()
    {
        var result = Run(Source);

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var source = GetGenerated(result, "Sample_App_GetContact.g.cs");

        source.Should().Contain(
            "global::System.Collections.Generic.IReadOnlyList<global::Elarion.Abstractions.Authorization.ResourceRequirementBinding<global::Sample.App.GetContact.Query>> __resourceBindings");
        source.Should().Contain(
            "new(typeof(global::Sample.App.Contact), new global::Elarion.Abstractions.Authorization.ResourceOperation(\"read\"), static __r => __r.Id),");
        source.Should().Contain("resourceBindings: __resourceBindings");
    }

    [Fact]
    public void RequireResource_EmitsExplicitResourceTypeNameOverride()
    {
        var overridden = Source.Replace(
            "[RequireResource(typeof(Contact), Operation = \"read\", Id = nameof(GetContact.Query.Id))]",
            "[RequireResource(typeof(Contact), Operation = \"read\", Id = nameof(GetContact.Query.Id), ResourceTypeName = \"Crm.Contact\")]");
        var result = Run(overridden);

        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        var source = GetGenerated(result, "Sample_App_GetContact.g.cs");

        source.Should().Contain(
            "new(typeof(global::Sample.App.Contact), new global::Elarion.Abstractions.Authorization.ResourceOperation(\"read\"), static __r => __r.Id, \"Crm.Contact\"),");
    }

    [Fact]
    public void RequireResource_UnresolvablePath_ReportsElauth002()
    {
        var result = Run(Source.Replace("Id = nameof(GetContact.Query.Id)", "Id = \"Missing\""));

        result.Diagnostics.Should().Contain(d => d.Id == "ELAUTH002");
    }

    [Fact]
    public void RequireResource_PrivateGetter_ReportsElauth002()
    {
        var privateGetter = Source.Replace(
            "public System.Guid Id { get; init; }",
            "public System.Guid Id { private get; init; }");

        Run(privateGetter).Diagnostics.Should().Contain(diagnostic => diagnostic.Id == "ELAUTH002");
    }

    private static GeneratorDriverRunResult Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "RequireResourceGeneratorTests",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new HandlerRegistrationGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static string GetGenerated(GeneratorDriverRunResult result, string fileName) =>
        result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences()
    {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();
        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
