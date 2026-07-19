using AwesomeAssertions;
using Elarion.EntityFrameworkCore.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

public sealed class ResourceFilterGeneratorTests {
    [Fact]
    public void ResourceFilter_OwnerOnly_GuidKey_EmitsAuthorizerAndOwnerPredicate() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        source.Should().Contain(
            "partial class ContactAccess : global::Elarion.Abstractions.Authorization.IQueryAuthorizer<global::Sample.Domain.Contact>");
        source.Should().Contain("public static ContactAccess Specification { get; } = new();");
        source.Should().Contain("if (!user.IsAuthenticated)");
        source.Should().Contain("if (!global::System.Guid.TryParse(user.UserId, out var __key0))");
        source.Should().Contain("return __e => __e.OwnerId == __key0;");
    }

    [Fact]
    public void ResourceFilter_OwnerAndTenant_ComposesScopeAndGrant() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId", TenantProperty = "TenantId")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // Owner is the grant (key0), tenant the scope (key1). Tenant comes from the "tenant" claim.
        source.Should().Contain(
            "global::System.Linq.Enumerable.FirstOrDefault(user.GetClaimValues(\"tenant\"))");
        // Scope AND (grant): tenant must match, and at least one grant.
        source.Should().Contain("return __e => __e.TenantId == __key1 && (__e.OwnerId == __key0);");
    }

    [Fact]
    public void ResourceFilter_StringOwner_ReadsUserIdDirectly() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Note {
                    public System.Guid Id { get; set; }
                    public string OwnerId { get; set; } = "";
                }

                [Elarion.Paging.ResourceFilter<Note>(OwnerProperty = "OwnerId")]
                public sealed partial class NoteAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_NoteAccess.ResourceFilter.g.cs");

        source.Should().Contain("var __key0 = user.UserId;");
        source.Should().Contain("return __e => __e.OwnerId == __key0;");
        source.Should().NotContain("TryParse");
    }

    [Fact]
    public void ResourceFilter_IntOwner_UsesInt32TryParse() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Doc {
                    public int Id { get; set; }
                    public int OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Doc>(OwnerProperty = "OwnerId")]
                public sealed partial class DocAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_DocAccess.ResourceFilter.g.cs");

        source.Should().Contain("if (!global::System.Int32.TryParse(user.UserId, out var __key0))");
    }

    [Fact]
    public void ResourceFilter_CustomTenantClaim_IsHonored() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public string TenantId { get; set; } = "";
                }

                [Elarion.Paging.ResourceFilter<Contact>(TenantProperty = "TenantId", TenantClaimType = "org")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // Tenant-only (scope), string key from the custom "org" claim, with a fail-closed null guard.
        source.Should().Contain(
            "var __key0 = global::System.Linq.Enumerable.FirstOrDefault(user.GetClaimValues(\"org\"));");
        source.Should().Contain("if (__key0 is null)");
        source.Should().Contain("return __e => __e.TenantId == __key0;");
    }

    [Fact]
    public void ResourceFilter_UnknownProperty_ReportsErrorAndGeneratesNothing() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "Missing")]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES001");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_UnsupportedPropertyType_ReportsError() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public bool OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES002");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_NullableProperty_ReportsUnsupported() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid? OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES002");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_NonPartialClass_ReportsError() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES003");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_NoRules_ReportsError() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES004");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_Shared_EmitsScopedAuthorizerWithGrantExists() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId", Shared = true, ResourceTypeName = "Contact")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var source = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        // Scoped service taking the grant source, plus a DI registration helper.
        source.Should()
            .Contain(
                "private readonly global::Elarion.Authorization.EntityFrameworkCore.IResourceGrantSource __grants;");
        source.Should()
            .Contain(
                "public ContactAccess(global::Elarion.Authorization.EntityFrameworkCore.IResourceGrantSource grants) => __grants = grants;");
        source.Should()
            .Contain(
                "public static void Register(global::Microsoft.Extensions.DependencyInjection.IServiceCollection services)");
        source.Should().NotContain("public static ContactAccess Specification");

        // The owner grant is OR-combined with a correlated EXISTS over the grants table for the caller's
        // user id OR any of their roles (a small IN over Roles), matched on the resource type, id, and operation.
        source.Should().Contain("var __ok0 = global::System.Guid.TryParse(user.UserId, out var __key0);");
        source.Should().Contain("var __roles = user.Roles;");
        source.Should()
            .Contain("global::System.Linq.Queryable.Any(__grantsQuery, __g => __g.ResourceType == \"Contact\"");
        source.Should().Contain("__g.ResourceId == __e.Id.ToString()");
        source.Should().Contain("__g.Operation == __op");
        source.Should().Contain("(__g.PrincipalKind == \"user\" && __g.PrincipalId == __uid)");
        source.Should()
            .Contain(
                "(__g.PrincipalKind == \"role\" && global::System.Linq.Enumerable.Contains(__roles, __g.PrincipalId))");
        source.Should()
            .Contain(
                "return __e => (__ok0 && __e.OwnerId == __key0) || global::System.Linq.Queryable.Any(__grantsQuery,");
    }

    [Fact]
    public void ResourceFilter_SharedWithoutResourceType_ReportsElres005() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(Shared = true)]
                public sealed partial class ContactAccess { }
            }
            """);

        result.Diagnostics.Should().Contain(d => d.Id == "ELRES005");
        result.GeneratedTrees.Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_SharedGeneratedSource_CompilesAgainstRuntime() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId", Shared = true, ResourceTypeName = "Contact")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var generated = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }
            }
            """;

        // Platform references (TPA) include the grants package, EF Core, and DI abstractions referenced by the
        // test project, so the generated shared authorizer compiles.
        CompileErrors(PlatformReferences(), entity, generated).Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_GeneratedSource_CompilesAgainstRuntime() {
        var result = Generate(
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId", TenantProperty = "TenantId")]
                public sealed partial class ContactAccess { }
            }
            """);

        NoErrors(result);
        var generated = GetGeneratedSource(result, "Sample_Domain_ContactAccess.ResourceFilter.g.cs");

        var entity =
            """
            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                    public System.Guid TenantId { get; set; }
                }
            }
            """;

        CompileErrors(entity, generated).Should().BeEmpty();
    }

    [Fact]
    public void ResourceFilter_ReusesOutputsAfterIrrelevantEdit() {
        var source =
            """
            namespace Elarion.Paging {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                public sealed class ResourceFilterAttribute<TEntity> : System.Attribute where TEntity : class {
                    public string? OwnerProperty { get; set; }
                    public string? TenantProperty { get; set; }
                    public string TenantClaimType { get; set; } = "tenant";
                    public bool Shared { get; set; }
                    public string? ResourceTypeName { get; set; }
                    public string IdProperty { get; set; } = "Id";
                }
            }

            namespace Sample.Domain {
                public sealed class Contact {
                    public System.Guid Id { get; set; }
                    public System.Guid OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed partial class ContactAccess { }
            }
            """;

        GeneratorCacheAssert.ReusesOutputsAfterIrrelevantEdit(
            new ResourceFilterGenerator(),
            source,
            "ResourceFilterTargets");
    }

    private static GeneratorDriverRunResult Generate(string testSource) {
        var source =
            $$"""
              namespace Elarion.Paging {
                  [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
                  public sealed class ResourceFilterAttribute<TEntity> : System.Attribute where TEntity : class {
                      public string? OwnerProperty { get; set; }
                      public string? TenantProperty { get; set; }
                      public string TenantClaimType { get; set; } = "tenant";
                      public bool Shared { get; set; }
                      public string? ResourceTypeName { get; set; }
                      public string IdProperty { get; set; } = "Id";
                  }
              }

              {{testSource}}
              """;

        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            "ResourceFilterGeneratorTests",
            [syntaxTree],
            PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new ResourceFilterGenerator());
        return driver.RunGenerators(compilation).GetRunResult();
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(params string[] sources) {
        return CompileErrors(RuntimeReferences(), sources);
    }

    private static IReadOnlyList<Diagnostic> CompileErrors(IReadOnlyList<MetadataReference> references,
        params string[] sources) {
        var compilation = CSharpCompilation.Create(
            "ResourceFilterCompileCheck",
            sources.Select(s => CSharpSyntaxTree.ParseText(s, new CSharpParseOptions(LanguageVersion.Preview))),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));

        return compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
    }

    private static void NoErrors(GeneratorDriverRunResult result) {
        result.Diagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    private static string GetGeneratedSource(GeneratorDriverRunResult result, string fileName) {
        return result.GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), fileName, StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static IReadOnlyList<MetadataReference> PlatformReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }

    private static IReadOnlyList<MetadataReference> RuntimeReferences() {
        var references = PlatformReferences().ToList();
        references.Add(MetadataReference.CreateFromFile(
            typeof(Elarion.Abstractions.Authorization.IQueryAuthorizer<>).Assembly.Location));
        return references;
    }
}
