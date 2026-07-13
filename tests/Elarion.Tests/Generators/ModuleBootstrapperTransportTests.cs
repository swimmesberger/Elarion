using AwesomeAssertions;
using Elarion.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Elarion.Tests.Generators;

/// <summary>
/// Tests the module-aware, feature-flag-gated transport mapping emitted by <see cref="AppModuleDiscoveryGenerator"/>:
/// per-module <c>Map{Module}Http</c> / <c>Add{Module}Handlers</c> / <c>Get{Module}McpMetadata</c> methods, gated
/// aggregates (<c>MapElarionEndpoints</c> / <c>RegisterHandlers</c> / <c>GetMcpMetadata</c>), per-handler transport
/// flags on the single shared registry, and core modules always mapped. Handlers and modules live in referenced
/// images and are consumed from generated Elarion manifest metadata.
/// </summary>
public sealed class ModuleBootstrapperTransportTests {
    private const string ModulesSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Modules;

        namespace Sample.Billing {
            [AppModule("Billing", Kind = AppModuleKind.Core)]
            public static class BillingModule { }

            [HttpEndpoint("invoices/{id}")]
            public sealed class GetInvoice : IHandler<GetInvoice.Query, Result<GetInvoice.Response>> {
                public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                public sealed record Response(string Number);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("INV"));
            }

            [Handler("invoices.get")]
            public sealed class GetInvoiceRpc : IHandler<GetInvoiceRpc.Query, Result<GetInvoiceRpc.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Number);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("INV"));
            }

            [Handler("invoices.archive", Transports = HandlerTransports.JsonRpc)]
            public sealed class ArchiveInvoiceRpc : IHandler<ArchiveInvoiceRpc.Command, Result<ArchiveInvoiceRpc.Response>> {
                public sealed record Command { public required System.Guid Id { get; init; } }
                public sealed record Response(bool Ok);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(true));
            }

            [Handler("invoices.summarize", Transports = HandlerTransports.Mcp)]
            public sealed class SummarizeInvoiceRpc : IHandler<SummarizeInvoiceRpc.Query, Result<SummarizeInvoiceRpc.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Summary);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("S"));
    
            [Handler("invoices.watch", Transports = HandlerTransports.Connection)]
            public sealed class WatchInvoiceRpc : IHandler<WatchInvoiceRpc.Query, Result<WatchInvoiceRpc.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string State);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("open"));
            }

            [Handler("invoices.annotate", Transports = HandlerTransports.JsonRpc | HandlerTransports.Connection)]
            public sealed class AnnotateInvoiceRpc : IHandler<AnnotateInvoiceRpc.Command, Result<AnnotateInvoiceRpc.Response>> {
                public sealed record Command { public required System.Guid Id { get; init; } }
                public sealed record Response(bool Ok);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(true));
            }
        }

            [Handler("invoices.watch", Transports = HandlerTransports.Connection)]
            public sealed class WatchInvoiceRpc : IHandler<WatchInvoiceRpc.Query, Result<WatchInvoiceRpc.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string State);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("open"));
            }

            [Handler("invoices.annotate", Transports = HandlerTransports.JsonRpc | HandlerTransports.Connection)]
            public sealed class AnnotateInvoiceRpc : IHandler<AnnotateInvoiceRpc.Command, Result<AnnotateInvoiceRpc.Response>> {
                public sealed record Command { public required System.Guid Id { get; init; } }
                public sealed record Response(bool Ok);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(true));
            }
        }

        namespace Sample.Shipping {
            [AppModule("Shipping")]
            public static class ShippingModule { }

            [HttpEndpoint("shipments")]
            public sealed class CreateShipment : IHandler<CreateShipment.Command, Result<CreateShipment.Response>> {
                public sealed record Command : ICommand { public required string Address { get; init; } }
                public sealed record Response(System.Guid Id);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
            }

            [Handler("shipments.create")]
            public sealed class CreateShipmentRpc : IHandler<CreateShipmentRpc.Command, Result<CreateShipmentRpc.Response>> {
                public sealed record Command { public required string Address { get; init; } }
                public sealed record Response(System.Guid Id);
                public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
            }
        }
        """;

    private const string HostSource =
        """
        using Elarion.AspNetCore;

        [assembly: GenerateModuleBootstrapper]
        """;

    [Fact]
    public void Bootstrapper_EmitsPerModuleTransportMethods() {
        var generated = RunGenerator(out _);

        // HTTP per-module methods plus one handler-registry method per module (the named bus).
        generated.Should().Contain(
            "public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapBillingHttp(");
        generated.Should().Contain(
            "public static global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder MapShippingHttp(");
        generated.Should().Contain(
            "public static global::Elarion.Abstractions.Dispatch.HandlerDispatcher AddBillingHandlers(");
        generated.Should().Contain(
            "public static global::Elarion.Abstractions.Dispatch.HandlerDispatcher AddShippingHandlers(");
        generated.Should().Contain(
            "global::Elarion.JsonRpc.Mcp.RpcMcpMethodMetadata[] GetBillingMcpMetadata()");

        // The per-module methods are extension methods on their receiver, so they read fluently at the
        // call site (e.g. app.MapGroup("/billing").MapBillingHttp(), dispatcher.AddBillingHandlers()).
        generated.Should().Contain("        this global::Microsoft.AspNetCore.Routing.IEndpointRouteBuilder app)");
        generated.Should().Contain("        this global::Elarion.Abstractions.Dispatch.HandlerDispatcher dispatcher)");

        // The per-module HTTP method maps that module's [HttpEndpoint] handler; the verb comes from the
        // request's CQRS marker (IQuery -> GET, ICommand -> POST).
        generated.Should().Contain("app.MapGet(\"invoices/{id}\",");
        generated.Should().Contain("app.MapPost(\"shipments\",");
        generated.Should().Contain(
            "[global::Microsoft.AspNetCore.Http.AsParameters] global::Sample.Billing.GetInvoice.Query request");
        // The per-module handler method maps that module's [Handler] operation onto the registry with its flags.
        generated.Should().Contain(
            "dispatcher.Map<global::Sample.Billing.GetInvoiceRpc.Query, global::Sample.Billing.GetInvoiceRpc.Response>(\"invoices.get\", global::Elarion.Abstractions.HandlerTransports.All);");

        // Each generated endpoint is tagged with its owning module so OpenAPI groups operations by module.
        generated.Should().Contain(".WithTags(\"Billing\")");
        generated.Should().Contain(".WithTags(\"Shipping\")");
        // No handler here is [Idempotent], so no idempotency marker is emitted.
        generated.Should().NotContain("ElarionIdempotentEndpointMetadata");
    }

    [Fact]
    public void Bootstrapper_MarksIdempotentHttpEndpoints() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Idempotency;
            using Elarion.Abstractions.Modules;

            namespace Sample.Payments {
                [AppModule("Payments", Kind = AppModuleKind.Core)]
                public static class PaymentsModule { }

                [HttpEndpoint("payments")]
                [Idempotent]
                public sealed class CreatePayment : IHandler<CreatePayment.Command, Result<CreatePayment.Response>> {
                    public sealed record Command : ICommand { public required string Amount { get; init; } }
                    public sealed record Response(System.Guid Id);
                    public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
                }

                [HttpEndpoint("payments/{id}")]
                public sealed class GetPayment : IHandler<GetPayment.Query, Result<GetPayment.Response>> {
                    public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                    public sealed record Response(System.Guid Id);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(request.Id));
                }
            }
            """;

        var generated = RunGenerator(modulesSource, out _);

        // The [Idempotent] POST endpoint carries the inert marker the OpenAPI package reads; the plain GET does not,
        // so the marker appears exactly once across both mapped endpoints.
        generated.Should().Contain("app.MapPost(\"payments\",");
        generated.Should().Contain("app.MapGet(\"payments/{id}\",");
        generated.Should().Contain(".WithMetadata(global::Elarion.AspNetCore.ElarionIdempotentEndpointMetadata.Instance)");
        generated.Should().Contain(".WithTags(\"Payments\")");
        generated.Split("ElarionIdempotentEndpointMetadata").Length.Should().Be(2);
    }

    [Fact]
    public void Bootstrapper_GatesFeatureModulesButNotCore() {
        var generated = RunGenerator(out _);

        // Core module (Billing) is mapped unconditionally on every transport — never wrapped in IsModuleEnabled.
        generated.Should().Contain("        MapBillingHttp(endpoints);");
        generated.Should().Contain("        AddBillingHandlers(dispatcher);");
        generated.Should().NotContain("if (IsModuleEnabled(configuration, \"Billing\"))");

        // Feature module (Shipping) is gated by its feature flag on every transport.
        generated.Should().Contain("if (IsModuleEnabled(configuration, \"Shipping\"))");
        generated.Should().Contain("MapShippingHttp(endpoints);");
        generated.Should().Contain("AddShippingHandlers(dispatcher);");
    }

    [Fact]
    public void Bootstrapper_EmitsGatedDispatcherAggregates() {
        var generated = RunGenerator(out _);

        generated.Should().Contain(
            "public static global::Elarion.Abstractions.Dispatch.HandlerDispatcher RegisterHandlers(");
        generated.Should().Contain(
            "public static global::Elarion.JsonRpc.Mcp.IRpcMcpMetadataSource GetMcpMetadata(");
        generated.Should().Contain(
            "global::Microsoft.Extensions.Configuration.IConfiguration configuration)");
        generated.Should().Contain("return dispatcher;");

        // The handler aggregate is an extension method on the shared registry
        // (dispatcher.RegisterHandlers(configuration)).
        generated.Should().Contain("        this global::Elarion.Abstractions.Dispatch.HandlerDispatcher dispatcher,");

        // The aggregates compose the per-module handler registration and MCP metadata.
        generated.Should().Contain("AddBillingHandlers(dispatcher);");
        generated.Should().Contain("methods.AddRange(GetBillingMcpMetadata());");
    }

    [Fact]
    public void Bootstrapper_SelectsTransportSurfacesPerHandler() {
        var generated = RunGenerator(out _);

        var handlers = Slice(generated, "HandlerDispatcher AddBillingHandlers(");
        var mcpMetadata = Slice(generated, "RpcMcpMethodMetadata[] GetBillingMcpMetadata()");

        // The single registry method maps every operation with its transport flags: "both" -> All,
        // a JSON-RPC-only -> JsonRpc, an MCP-only -> Mcp.
        handlers.Should().Contain("\"invoices.get\", global::Elarion.Abstractions.HandlerTransports.All");
        handlers.Should().Contain("\"invoices.archive\", global::Elarion.Abstractions.HandlerTransports.JsonRpc");
        handlers.Should().Contain("\"invoices.summarize\", global::Elarion.Abstractions.HandlerTransports.Mcp");
        // Connection-only and composed flags survive the model round-trip (ADR-0053).
        handlers.Should().Contain("\"invoices.watch\", global::Elarion.Abstractions.HandlerTransports.Connection");
        handlers.Should().Contain(
            "\"invoices.annotate\", global::Elarion.Abstractions.HandlerTransports.JsonRpc | global::Elarion.Abstractions.HandlerTransports.Connection");

        // The MCP tool table mirrors the MCP surface: the "both" and MCP-only operations, never the
        // JSON-RPC-only or connection-only ones.
        mcpMetadata.Should().Contain("MethodName = \"invoices.get\"")
            .And.Contain("MethodName = \"invoices.summarize\"");
        mcpMetadata.Should().NotContain("invoices.archive");
        mcpMetadata.Should().NotContain("invoices.watch");
        mcpMetadata.Should().NotContain("invoices.annotate");
    }

    [Fact]
    public void Bootstrapper_SanitizesTransportMethodNamesForNonIdentifierModuleNames() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            namespace Sample.BillingInvoicing {
                [AppModule("Billing.Invoicing")]
                public static class BillingInvoicingModule { }

                [HttpEndpoint("invoices")]
                [Handler("invoices.list")]
                public sealed class ListInvoices : IHandler<ListInvoices.Query, Result<ListInvoices.Response>> {
                    public sealed record Query : IQuery;
                    public sealed record Response(string Number);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("INV"));
                }
            }
            """;

        var generated = RunGenerator(modulesSource, out var compilationWithGenerated);

        generated.Should().Contain("MapBilling_Invoicing_")
            .And.Contain("AddBilling_Invoicing_");
        generated.Should().NotContain("MapBilling.InvoicingHttp");
        generated.Should().Contain("IsModuleEnabled(configuration, \"Billing.Invoicing\")");
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_MapsFileResponseEndpointThroughFileTranslation() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            namespace Sample.Files {
                [AppModule("Files", Kind = AppModuleKind.Core)]
                public static class FilesModule { }

                [HttpEndpoint("files/{id}")]
                public sealed class DownloadFile : IHandler<DownloadFile.Query, Result<ElarionFile>> {
                    public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                    public ValueTask<Result<ElarionFile>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<ElarionFile>>(
                            new ElarionFile(new byte[] { 1 }, "application/octet-stream"));
                }
            }
            """;

        var generated = RunGenerator(modulesSource, out var compilationWithGenerated);

        // A Result<ElarionFile> endpoint goes through the file translation and advertises a binary payload
        // (plus the marker the OpenAPI package upgrades into type: string, format: binary).
        generated.Should().Contain("app.MapGet(\"files/{id}\",");
        generated.Should().Contain(".ToFileResult(await handler.HandleAsync(request, ct)))");
        generated.Should().Contain(".Produces(200, null, \"application/octet-stream\")");
        generated.Should().Contain(".WithMetadata(global::Elarion.AspNetCore.ElarionFileEndpointMetadata.Instance)");
        generated.Should().NotContain(".Produces<global::Elarion.Abstractions.ElarionFile>");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_CallsHostDeclaredModuleEndpointsInsideModuleGate() {
        const string hostSource =
            """
            using Elarion.AspNetCore;
            using Microsoft.AspNetCore.Routing;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.Web {
                [ModuleEndpoints("Shipping")]
                internal static class ShippingWebEndpoints {
                    public static void MapEndpoints(IEndpointRouteBuilder endpoints) { }
                    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder endpoints) => endpoints;
                }
            }
            """;

        var modulesReference = CompileToImage(ModulesSource, "Sample.Modules");
        var generated = RunGenerator(hostSource, [modulesReference], out var compilationWithGenerated);

        // The host-declared hooks run inside the feature module's gate, and the generated [HttpEndpoint] routes
        // map onto the builder the contributor's group hook returns — no hand-written IsModuleEnabled re-check.
        generated.Should().Contain("if (IsModuleEnabled(configuration, \"Shipping\"))");
        generated.Should().Contain(
            "var ShippingEndpoints = global::Host.Web.ShippingWebEndpoints.ConfigureEndpointGroup(endpoints);");
        generated.Should().Contain("global::Host.Web.ShippingWebEndpoints.MapEndpoints(ShippingEndpoints);");
        generated.Should().Contain("MapShippingHttp(ShippingEndpoints);");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_ChainsContributorGroupHookOntoModuleGroupHook() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Microsoft.AspNetCore.Routing;

            namespace Sample.Billing {
                [AppModule("Billing", Kind = AppModuleKind.Core)]
                public static class BillingModule {
                    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder root) => root;
                }

                [HttpEndpoint("invoices/{id}")]
                public sealed class GetInvoice : IHandler<GetInvoice.Query, Result<GetInvoice.Response>> {
                    public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                    public sealed record Response(string Number);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("INV"));
                }
            }
            """;

        const string hostSource =
            """
            using Elarion.AspNetCore;
            using Microsoft.AspNetCore.Routing;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.Web {
                [ModuleEndpoints("Billing")]
                internal static class BillingExtraEndpoints {
                    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder endpoints) => endpoints;
                }
            }
            """;

        var modulesReference = CompileToImage(modulesSource, "Sample.Modules");
        var generated = RunGenerator(hostSource, [modulesReference], out var compilationWithGenerated);

        // The module's own group hook wraps first; the contributor's chains onto the result.
        generated.Should().Contain(
            "var BillingEndpoints = global::Sample.Billing.BillingModule.ConfigureEndpointGroup(endpoints);");
        generated.Should().Contain(
            "BillingEndpoints = global::Host.Web.BillingExtraEndpoints.ConfigureEndpointGroup(BillingEndpoints);");
        generated.Should().Contain("MapBillingHttp(BillingEndpoints);");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_ConsumesModuleEndpointsFromReferencedManifest() {
        // A web companion assembly publishes its [ModuleEndpoints] contributor through the manifest; the host
        // bootstrapper calls it without the companion being part of the host compilation.
        var module = EncodeFields(
            "Manifest", "ManifestOnly", "global::ManifestOnly.ManifestModule", null, "0", "0", "0", "0", "0", "");
        var hooks = EncodeFields("Manifest", "global::ManifestOnly.ManifestWebEndpoints", "1", "0");

        var librarySource = $$"""
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "1")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Module.v1", "{{module}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.ModuleEndpoints.v1", "{{hooks}}")]

            namespace ManifestOnly;

            public static class ManifestModule { }

            public static class ManifestWebEndpoints {
                public static void MapEndpoints(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder endpoints) { }
            }
            """;

        var libraryReference = CompileToImage(librarySource, "Sample.ManifestOnly");
        var generated = RunGenerator([libraryReference], out var compilationWithGenerated);

        generated.Should().Contain("if (IsModuleEnabled(configuration, \"Manifest\"))");
        generated.Should().Contain("global::ManifestOnly.ManifestWebEndpoints.MapEndpoints(endpoints);");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_WarnsAndSkipsModuleEndpointsNamingUnknownModule() {
        const string hostSource =
            """
            using Elarion.AspNetCore;
            using Microsoft.AspNetCore.Routing;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.Web {
                [ModuleEndpoints("Nowhere")]
                internal static class NowhereEndpoints {
                    public static void MapEndpoints(IEndpointRouteBuilder endpoints) { }
                }
            }
            """;

        var result = RunGeneratorRun(hostSource, ModulesSource);

        result.Diagnostics.Should().Contain(d => d.Id == "ELMOD004" && d.Severity == DiagnosticSeverity.Warning);
        var generated = result.GeneratedTrees
            .Single(tree => string.Equals(
                Path.GetFileName(tree.FilePath), "ElarionBootstrapper.g.cs", StringComparison.Ordinal))
            .GetText(TestContext.Current.CancellationToken)
            .ToString();
        generated.Should().NotContain("NowhereEndpoints");
    }

    [Fact]
    public void Bootstrapper_RoutesModuleEndpointsThroughConfigureEndpointGroupHook() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Microsoft.AspNetCore.Routing;

            namespace Sample.Billing {
                [AppModule("Billing", Kind = AppModuleKind.Core)]
                public static class BillingModule {
                    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder root) => root;
                }

                [HttpEndpoint("invoices/{id}")]
                public sealed class GetInvoice : IHandler<GetInvoice.Query, Result<GetInvoice.Response>> {
                    public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                    public sealed record Response(string Number);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("INV"));
                }
            }

            namespace Sample.Shipping {
                [AppModule("Shipping")]
                public static class ShippingModule {
                    public static IEndpointRouteBuilder ConfigureEndpointGroup(IEndpointRouteBuilder root) => root;
                }

                [HttpEndpoint("shipments")]
                public sealed class CreateShipment : IHandler<CreateShipment.Command, Result<CreateShipment.Response>> {
                    public sealed record Command : ICommand { public required string Address { get; init; } }
                    public sealed record Response(System.Guid Id);
                    public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
                }
            }
            """;

        var generated = RunGenerator(modulesSource, out var compilationWithGenerated);

        // The core module's generated routes are mapped onto the builder its hook returns, not the root.
        generated.Should().Contain(
            "var BillingEndpoints = global::Sample.Billing.BillingModule.ConfigureEndpointGroup(endpoints);");
        generated.Should().Contain("MapBillingHttp(BillingEndpoints);");

        // The feature module's hook is invoked inside its feature gate.
        generated.Should().Contain("if (IsModuleEnabled(configuration, \"Shipping\"))");
        generated.Should().Contain(
            "var ShippingEndpoints = global::Sample.Shipping.ShippingModule.ConfigureEndpointGroup(endpoints);");
        generated.Should().Contain("MapShippingHttp(ShippingEndpoints);");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_WarnsWhenModulesShareNamespace() {
        const string modulesSource =
            """
            using Elarion.Abstractions.Modules;

            namespace Sample.Inventory {
                [AppModule("Inventory")]
                public static class InventoryModule { }

                [AppModule("Catalog")]
                public static class CatalogModule { }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(modulesSource);

        diagnostics.Should().Contain(d => d.Id == "ELMOD001" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Bootstrapper_ConsumesReferencedAssemblyManifest() {
        var manifestReference = CompileToImage(ManifestOnlySource(), "Sample.ManifestOnly");

        var generated = RunGenerator([manifestReference], out var compilationWithGenerated);

        generated.Should().Contain("MapManifestHttp(endpoints);");
        generated.Should().Contain("AddManifestHandlers(dispatcher);");
        generated.Should().Contain("app.MapGet(\"manifest\",");
        generated.Should().Contain(
            "dispatcher.Map<global::ManifestOnly.GetManifest.Query, global::ManifestOnly.GetManifest.Response>(\"manifest.get\", global::Elarion.Abstractions.HandlerTransports.All);");
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_RegistersResourceFiltersFromReferencedManifest_Gated() {
        var filterReference = CompileToImage(ResourceFilterLibSource(), "Sample.FilterLib");

        var generated = RunGenerator([filterReference], out var compilationWithGenerated);

        // The feature module's filters are registered through AddElarion, gated by the module flag.
        Slice(generated, "public static void AddElarion(")
            .Should().Contain("if (IsModuleEnabled(configuration, \"FilterLib\"))")
            .And.Contain("AddFilterLibResourceFilters(services);");

        // A field-only spec registers its static Specification as a singleton; a shared spec registers scoped.
        var method = Slice(generated, "IServiceCollection AddFilterLibResourceFilters(");
        method.Should().Contain(
            "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<global::Elarion.Abstractions.Authorization.IQueryAuthorizer<global::FilterLib.Contact>>(services, global::FilterLib.ContactAccess.Specification);");
        method.Should().Contain(
            "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<global::Elarion.Abstractions.Authorization.IQueryAuthorizer<global::FilterLib.Order>, global::FilterLib.OrderAccess>(services);");

        // The registrations reference the referenced assembly's real types, so the generated code compiles.
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_DiscoversTransportHandlersInHostCompilation() {
        // The single-project layout: Program, [AppModule], and every handler live in one compilation with
        // [assembly: GenerateModuleBootstrapper] — there is no referenced manifest, so the bootstrapper must
        // discover the transport handlers directly from the current compilation.
        const string hostSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.AspNetCore;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.App {
                [AppModule("App", Kind = AppModuleKind.Core)]
                public static class AppModule { }

                [HttpEndpoint("things/{id}")]
                [Handler]
                public sealed class GetThing : IHandler<GetThing.Query, Result<GetThing.Response>> {
                    public sealed record Query : IQuery { public required System.Guid Id { get; init; } }
                    public sealed record Response(string Name);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("thing"));
                }

                [Handler("things.archive", Transports = HandlerTransports.Mcp)]
                public sealed class ArchiveThing : IHandler<ArchiveThing.Command, Result<ArchiveThing.Response>> {
                    public sealed record Command { public required System.Guid Id { get; init; } }
                    public sealed record Response(bool Ok);
                    public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(true));
                }
            }

            namespace Host.Shipping {
                [AppModule("Shipping")]
                public static class ShippingModule { }

                [HttpEndpoint("shipments")]
                public sealed class CreateShipment : IHandler<CreateShipment.Command, Result<CreateShipment.Response>> {
                    public sealed record Command : ICommand { public required string Address { get; init; } }
                    public sealed record Response(System.Guid Id);
                    public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
                }
            }
            """;

        var generated = RunGenerator(hostSource, [], out var compilationWithGenerated);

        // All three transports are wired from the host compilation alone.
        generated.Should().Contain("MapAppHttp(endpoints);");
        generated.Should().Contain("app.MapGet(\"things/{id}\",");
        generated.Should().Contain(
            "dispatcher.Map<global::Host.App.GetThing.Query, global::Host.App.GetThing.Response>(\"app.getThing\", global::Elarion.Abstractions.HandlerTransports.All);");
        generated.Should().Contain(
            "dispatcher.Map<global::Host.App.ArchiveThing.Command, global::Host.App.ArchiveThing.Response>(\"things.archive\", global::Elarion.Abstractions.HandlerTransports.Mcp);");
        generated.Should().Contain("GetAppMcpMetadata()");

        // Feature-module gating applies to host-compilation handlers exactly like referenced ones.
        generated.Should().Contain("if (IsModuleEnabled(configuration, \"Shipping\"))");
        generated.Should().Contain("MapShippingHttp(endpoints);");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_WarnsOnHostCompilationHandlerUnderNoModule() {
        const string hostSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.AspNetCore;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.Orphan {
                [HttpEndpoint("orphans")]
                [Handler("orphans.list")]
                public sealed class ListOrphans : IHandler<ListOrphans.Query, Result<ListOrphans.Response>> {
                    public sealed record Query : IQuery;
                    public sealed record Response(int Count);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(0));
                }
            }
            """;

        var result = RunGeneratorRun(hostSource, ModulesSource);

        // A host-compilation handler outside every module is mapped ungated and warned, mirroring manifests.
        result.Diagnostics.Should().Contain(d => d.Id == "ELHTTP003" && d.Severity == DiagnosticSeverity.Warning);
        result.Diagnostics.Should().Contain(d => d.Id == "ELRPC001" && d.Severity == DiagnosticSeverity.Warning);
        var generated = result.GeneratedTrees
            .Single(tree => string.Equals(
                Path.GetFileName(tree.FilePath), "ElarionBootstrapper.g.cs", StringComparison.Ordinal))
            .GetText(TestContext.Current.CancellationToken)
            .ToString();
        generated.Should().Contain("app.MapGet(\"orphans\",");
        generated.Should().Contain("\"orphans.list\", global::Elarion.Abstractions.HandlerTransports.All");
    }

    [Fact]
    public void Bootstrapper_MergesHostCompilationHandlersWithReferencedManifests() {
        // Two-project layout with extra handlers in the host itself: both sources contribute to the maps.
        const string hostSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.AspNetCore;

            [assembly: GenerateModuleBootstrapper]

            namespace Sample.Billing.Extras {
                [Handler("invoices.export")]
                public sealed class ExportInvoices : IHandler<ExportInvoices.Query, Result<ExportInvoices.Response>> {
                    public sealed record Query;
                    public sealed record Response(string Url);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("u"));
                }
            }
            """;

        var modulesReference = CompileToImage(ModulesSource, "Sample.Modules");
        var generated = RunGenerator(hostSource, [modulesReference], out var compilationWithGenerated);

        // The host-declared handler lands in the referenced [AppModule]'s bucket by namespace prefix.
        var handlers = Slice(generated, "HandlerDispatcher AddBillingHandlers(");
        handlers.Should().Contain("\"invoices.export\"");
        handlers.Should().Contain("\"invoices.get\"");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_DeduplicatesHandlerPresentInBothCompilationAndManifest() {
        // A manifest entry naming the same operation and types as a host-compilation handler collapses to one
        // registration (the local entry wins, mirroring module deduplication).
        var rpc = EncodeFields(
            "things.get",
            "Host.App",
            "global::Host.App.GetThing.Query",
            "global::Host.App.GetThing.Response",
            null,
            "1",
            "1",
            null,
            string.Empty,
            "0",
            "0");
        var librarySource = $$"""
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "1")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.RpcMethod.v1", "{{rpc}}")]

            namespace ManifestOnly { public static class Placeholder { } }
            """;

        const string hostSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;
            using Elarion.AspNetCore;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.App {
                [AppModule("App", Kind = AppModuleKind.Core)]
                public static class AppModule { }

                [Handler("things.get")]
                public sealed class GetThing : IHandler<GetThing.Query, Result<GetThing.Response>> {
                    public sealed record Query { public required System.Guid Id { get; init; } }
                    public sealed record Response(string Name);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("thing"));
                }
            }
            """;

        var libraryReference = CompileToImage(librarySource, "Sample.ManifestDuplicate");
        var result = RunGeneratorRunWithReferences(hostSource, [libraryReference]);

        var generated = result.GeneratedTrees
            .Single(tree => string.Equals(
                Path.GetFileName(tree.FilePath), "ElarionBootstrapper.g.cs", StringComparison.Ordinal))
            .GetText(TestContext.Current.CancellationToken)
            .ToString();

        // One registration, no ELRPC003 duplicate-name warning.
        generated.Split("dispatcher.Map<global::Host.App.GetThing.Query").Length.Should().Be(2);
        result.Diagnostics.Should().NotContain(d => d.Id == "ELRPC003");
    }

    [Fact]
    public void Bootstrapper_RegistersResourceFiltersFromHostCompilation_Gated() {
        const string hostSource =
            """
            using System;
            using System.Linq.Expressions;
            using Elarion.Abstractions.Authorization;
            using Elarion.Abstractions.Identity;
            using Elarion.Abstractions.Modules;
            using Elarion.AspNetCore;

            [assembly: GenerateModuleBootstrapper]

            namespace Host.Contacts {
                [AppModule("Contacts")]
                public static class ContactsModule { }

                public sealed class Contact {
                    public Guid Id { get; set; }
                    public Guid OwnerId { get; set; }
                }

                [Elarion.Paging.ResourceFilter<Contact>(OwnerProperty = "OwnerId")]
                public sealed class ContactAccess : IQueryAuthorizer<Contact> {
                    public static ContactAccess Specification { get; } = new();
                    private ContactAccess() { }
                    public Expression<Func<Contact, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) => null;
                }
            }
            """;

        var generated = RunGenerator(hostSource, [], out var compilationWithGenerated);

        // The host-compilation spec registers through AddElarion, gated by its module flag.
        Slice(generated, "public static void AddElarion(")
            .Should().Contain("if (IsModuleEnabled(configuration, \"Contacts\"))")
            .And.Contain("AddContactsResourceFilters(services);");
        Slice(generated, "IServiceCollection AddContactsResourceFilters(")
            .Should().Contain(
                "global::Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<global::Elarion.Abstractions.Authorization.IQueryAuthorizer<global::Host.Contacts.Contact>>(services, global::Host.Contacts.ContactAccess.Specification);");

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_GeneratedCodeCompiles() {
        RunGenerator(out var compilationWithGenerated);

        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_EmitsClientCapabilityManifest_FromReferencedClientFeatures() {
        // A module declaring [ClientFeatures] in a referenced assembly — the names must survive the manifest
        // round-trip (encode → image → decode), and every module appears with its IsModuleEnabled state.
        var modulesReference = CompileToImage(
            """
            using Elarion.Abstractions.Modules;

            namespace Sample.ClientFeatures {
                [AppModule("Billing")]
                [ClientFeatures("new-checkout", "dashboard-v2")]
                public static class BillingModule { }

                [AppModule("Core", Kind = AppModuleKind.Core)]
                public static class CoreModule { }
            }
            """,
            "Sample.ClientFeatures");

        var generated = RunGenerator([modulesReference], out var compilationWithGenerated);

        var method = Slice(generated, "GetClientCapabilityManifest(");
        method.Should().Contain(
                "new global::Elarion.Abstractions.Modules.ClientModuleManifest { Name = \"Billing\", Enabled = IsModuleEnabled(configuration, \"Billing\"), Features = new string[] { \"new-checkout\", \"dashboard-v2\" } }")
            .And.Contain(
                "new global::Elarion.Abstractions.Modules.ClientModuleManifest { Name = \"Core\", Enabled = IsModuleEnabled(configuration, \"Core\"), Features = global::System.Array.Empty<string>() }");

        // The generated code references the real Elarion.Session manifest types, so it compiles.
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_EmitsModulesOnlyClientCapabilityManifest_WhenNoModuleExposesClientFeatures() {
        // No module declares [ClientFeatures], but the method is still emitted with a modules-only manifest so
        // AddElarionSession(configuration.GetClientCapabilityManifest()) compiles for every host — the session
        // bootstrap still projects per-user module enablement from it (an empty manifest would drop that).
        var generated = RunGenerator(out var compilationWithGenerated);

        var method = Slice(generated, "GetClientCapabilityManifest(");
        method.Should().Contain(
                "new global::Elarion.Abstractions.Modules.ClientModuleManifest { Name = \"Billing\", Enabled = IsModuleEnabled(configuration, \"Billing\"), Features = global::System.Array.Empty<string>() }")
            .And.Contain(
                "new global::Elarion.Abstractions.Modules.ClientModuleManifest { Name = \"Shipping\", Enabled = IsModuleEnabled(configuration, \"Shipping\"), Features = global::System.Array.Empty<string>() }");

        // The generated code references the real Elarion.Session manifest types, so it compiles.
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    [Fact]
    public void Bootstrapper_IsDeterministic() {
        var first = RunGenerator(out _);
        var second = RunGenerator(out _);

        second.Should().Be(first);
    }

    [Fact]
    public void Bootstrapper_WarnsOnDuplicateOperationName() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            namespace Sample.Dup {
                [AppModule("Dup", Kind = AppModuleKind.Core)]
                public static class DupModule { }

                [Handler("dup.op")]
                public sealed class FirstOp : IHandler<FirstOp.Query, Result<FirstOp.Response>> {
                    public sealed record Query;
                    public sealed record Response(string V);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("a"));
                }

                [Handler("dup.op")]
                public sealed class SecondOp : IHandler<SecondOp.Query, Result<SecondOp.Response>> {
                    public sealed record Query;
                    public sealed record Response(string V);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("b"));
                }
            }
            """;

        var diagnostics = RunGeneratorDiagnostics(modulesSource);

        diagnostics.Should().Contain(d => d.Id == "ELRPC003" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Bootstrapper_InfersOperationName_FromModuleAndHandlerName() {
        const string modulesSource =
            """
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;
            using Elarion.Abstractions.Modules;

            namespace Sample.Catalog {
                [AppModule("Catalog", Kind = AppModuleKind.Core)]
                public static class CatalogModule { }

                [Handler]
                public sealed class CreateProduct : IHandler<CreateProduct.Command, Result<CreateProduct.Response>> {
                    public sealed record Command { public required string Name { get; init; } }
                    public sealed record Response(System.Guid Id);
                    public ValueTask<Result<Response>> HandleAsync(Command request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response(System.Guid.Empty));
                }

                [Handler(Transports = HandlerTransports.JsonRpc)]
                public sealed class GetProductHandler : IHandler<GetProductHandler.Query, Result<GetProductHandler.Response>> {
                    public sealed record Query { public required System.Guid Id { get; init; } }
                    public sealed record Response(string Name);
                    public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                        ValueTask.FromResult<Result<Response>>(new Response("p"));
                }
            }
            """;

        var generated = RunGenerator(modulesSource, out var compilationWithGenerated);

        // No explicit name -> {module}.{handler-name minus Handler/Command/Query/Request suffix, camelCased}.
        generated.Should().Contain("\"catalog.createProduct\", global::Elarion.Abstractions.HandlerTransports.All");
        generated.Should().Contain("\"catalog.getProduct\", global::Elarion.Abstractions.HandlerTransports.JsonRpc");
        compilationWithGenerated.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();
    }

    // Extracts a single emitted method body (from its signature marker to the matching closing brace) so a test can
    // assert which transport surface a handler landed on without being confused by call sites elsewhere in the file.
    private static string Slice(string source, string signatureMarker) {
        var start = source.IndexOf(signatureMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the emitted source should contain {0}", signatureMarker);

        var braceStart = source.IndexOf('{', start);
        braceStart.Should().BeGreaterThanOrEqualTo(0);

        var depth = 0;
        for (var i = braceStart; i < source.Length; i++) {
            if (source[i] == '{') {
                depth++;
            } else if (source[i] == '}') {
                depth--;
                if (depth == 0) {
                    return source.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        return source[braceStart..];
    }

    private static string RunGenerator(out Compilation compilationWithGenerated) =>
        RunGenerator(ModulesSource, out compilationWithGenerated);

    private static string RunGenerator(string modulesSource, out Compilation compilationWithGenerated) {
        var modulesReference = CompileToImage(modulesSource, "Sample.Modules");
        return RunGenerator([modulesReference], out compilationWithGenerated);
    }

    private static string RunGenerator(IReadOnlyList<MetadataReference> moduleReferences, out Compilation compilationWithGenerated) =>
        RunGenerator(HostSource, moduleReferences, out compilationWithGenerated);

    private static string RunGenerator(
        string hostSource,
        IReadOnlyList<MetadataReference> moduleReferences,
        out Compilation compilationWithGenerated) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var hostTree = CSharpSyntaxTree.ParseText(hostSource, parseOptions);
        var references = CreateMetadataReferences().Concat(moduleReferences).ToArray();
        var hostCompilation = CSharpCompilation.Create(
            "Host",
            [hostTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // The module-services skeleton generator always runs alongside the bootstrapper in a real build; a
        // host-compilation [AppModule] needs its generated ConfigureDefaultServices sibling to compile.
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new AppModuleDiscoveryGenerator().AsSourceGenerator(), new ModuleDefaultServicesGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            hostCompilation, out compilationWithGenerated, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        return driver.GetRunResult().GeneratedTrees
            .Single(tree => string.Equals(Path.GetFileName(tree.FilePath), "ElarionBootstrapper.g.cs", StringComparison.Ordinal))
            .GetText()
            .ToString();
    }

    private static string ManifestOnlySource() {
        var module = EncodeFields(
            "Manifest",
            "ManifestOnly",
            "global::ManifestOnly.ManifestModule",
            null,
            "0",
            "0",
            "0",
            "0",
            "0",
            // The client-features blob (10th field) — empty for a module with no [ClientFeatures].
            "");
        var http = EncodeFields(
            "ManifestOnly.GetManifest",
            "ManifestOnly",
            "global::ManifestOnly.GetManifest.Query",
            "global::ManifestOnly.GetManifest.Response",
            "manifest",
            "Get",
            "1",
            "0",
            "0",
            null,
            "0");
        var rpc = EncodeFields(
            "manifest.get",
            "ManifestOnly",
            "global::ManifestOnly.GetManifest.Query",
            "global::ManifestOnly.GetManifest.Response",
            null,
            "1",
            "1",
            null,
            string.Empty,
            "0",
            "0",
            // OnConnection (12th field, appended by ADR-0053) — "1" so the entry decodes as All.
            "1");

        return $$"""
            using System.Threading;
            using System.Threading.Tasks;
            using Elarion.Abstractions;

            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "1")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Module.v1", "{{module}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.HttpEndpoint.v1", "{{http}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.RpcMethod.v1", "{{rpc}}")]

            namespace ManifestOnly;

            public static class ManifestModule { }

            public sealed class GetManifest : IHandler<GetManifest.Query, Result<GetManifest.Response>> {
                public sealed record Query { public required System.Guid Id { get; init; } }
                public sealed record Response(string Name);
                public ValueTask<Result<Response>> HandleAsync(Query request, CancellationToken ct) =>
                    ValueTask.FromResult<Result<Response>>(new Response("manifest"));
            }
            """;
    }

    private static string ResourceFilterLibSource() {
        // A feature module (IsCore = 0) so its filter registration is gated by the module flag.
        var module = EncodeFields(
            "FilterLib", "FilterLib", "global::FilterLib.FilterModule", null, "0", "0", "0", "0", "0", "");
        var owner = EncodeFields(
            "global::FilterLib.ContactAccess", "global::FilterLib.Contact", "FilterLib", "0");
        var shared = EncodeFields(
            "global::FilterLib.OrderAccess", "global::FilterLib.Order", "FilterLib", "1");

        return $$"""
            using System;
            using System.Linq.Expressions;
            using Elarion.Abstractions.Authorization;
            using Elarion.Abstractions.Identity;
            using Elarion.Authorization.EntityFrameworkCore;

            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Schema", "1")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.Module.v1", "{{module}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.ResourceFilter.v1", "{{owner}}")]
            [assembly: System.Reflection.AssemblyMetadata("Elarion.Manifest.ResourceFilter.v1", "{{shared}}")]

            namespace FilterLib;

            public static class FilterModule { }

            public sealed class Contact { public Guid Id { get; set; } }
            public sealed class Order { public Guid Id { get; set; } }

            public sealed class ContactAccess : IQueryAuthorizer<Contact> {
                public static ContactAccess Specification { get; } = new();
                private ContactAccess() { }
                public Expression<Func<Contact, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) => null;
            }

            public sealed class OrderAccess : IQueryAuthorizer<Order> {
                public OrderAccess(IResourceGrantSource grants) { }
                public Expression<Func<Order, bool>>? GetFilter(ICurrentUser user, ResourceOperation operation) => null;
            }
            """;
    }

    private static string EncodeFields(params string?[] fields) {
        var result = new System.Text.StringBuilder();
        foreach (var field in fields) {
            if (field is null) {
                result.Append("-1:");
                continue;
            }

            result.Append(field.Length);
            result.Append(':');
            result.Append(field);
        }

        return result.ToString();
    }

    private static IReadOnlyList<Diagnostic> RunGeneratorDiagnostics(string modulesSource) =>
        RunGeneratorRun(HostSource, modulesSource).Diagnostics;

    private static GeneratorDriverRunResult RunGeneratorRun(string hostSource, string modulesSource) =>
        RunGeneratorRunWithReferences(hostSource, [CompileToImage(modulesSource, "Sample.Modules")]);

    private static GeneratorDriverRunResult RunGeneratorRunWithReferences(
        string hostSource,
        IReadOnlyList<MetadataReference> moduleReferences) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var hostTree = CSharpSyntaxTree.ParseText(hostSource, parseOptions);
        var references = CreateMetadataReferences().Concat(moduleReferences).ToArray();
        var hostCompilation = CSharpCompilation.Create(
            "Host",
            [hostTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new AppModuleDiscoveryGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGenerators(hostCompilation, TestContext.Current.CancellationToken);

        return driver.GetRunResult();
    }

    private static MetadataReference CompileToImage(string source, string assemblyName) {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions);
        Compilation compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            CreateMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken)
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new ElarionManifestGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out compilation, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty();

        using var stream = new MemoryStream();
        compilation.Emit(stream, cancellationToken: TestContext.Current.CancellationToken).Success.Should().BeTrue();
        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static IReadOnlyList<MetadataReference> CreateMetadataReferences() {
        var trustedPlatformAssemblies = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");

        trustedPlatformAssemblies.Should().NotBeNull();

        return trustedPlatformAssemblies!
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();
    }
}
