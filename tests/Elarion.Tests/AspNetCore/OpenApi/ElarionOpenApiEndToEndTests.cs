using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Elarion.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore.OpenApi;

/// <summary>
/// End-to-end test that boots a real Kestrel host, maps endpoints exactly as <c>AppModuleDiscoveryGenerator</c>
/// emits them for the OpenAPI package (issue #131 / ADR-0071): the AOT-safe RequestDelegate registration and the
/// deterministic metadata chain — name, module tag, <see cref="ProducesResponseTypeMetadata"/>, error metadata,
/// markers, per-member <see cref="ElarionHttpParameterBindingMetadata"/> pointing at the flattened
/// <c>[From*]</c>-attributed API shape parameters (or <see cref="AcceptsMetadata"/> for a JSON body), and the
/// never-invoked shape <c>MethodInfo</c> that gates ApiExplorer's description. Registers
/// <see cref="ElarionOpenApiServiceCollectionExtensions.AddElarionOpenApi(IServiceCollection, Action{Microsoft.AspNetCore.OpenApi.OpenApiOptions}?)"/>,
/// and reads the served OpenAPI document. Reflection is off (the repo default) and the DTOs live only in a
/// source-generated <see cref="JsonSerializerContext"/>, so a passing test proves the canonical-JSON wiring makes
/// schema generation resolve body types through the source-gen resolver chain without reflection.
/// </summary>
public sealed partial class ElarionOpenApiEndToEndTests {
    private sealed record CreatePaymentCommand {
        public required string Amount { get; init; }
    }

    private sealed record CreatePaymentResponse(Guid Id);

    private sealed record GetPaymentQuery {
        public required Guid Id { get; init; }
    }

    private sealed record GetPaymentResponse(Guid Id, string Amount);

    private sealed record RegisterCustomerCommand {
        [EmailAddress] public required string Email { get; init; }

        [StringLength(100, MinimumLength = 3)] public required string DisplayName { get; init; }

        [Range(1, 120)] public required int Age { get; init; }

        // Declared before the unlimited members, so a limit that leaked into the shared UploadedFile schema would show.
        [UploadLimits(MaxBytes = 1024, ContentTypes = ["application/pdf"])]
        public required UploadedFile Contract { get; init; }

        public UploadedFile? Passport { get; init; }

        [UploadLimits(ContentTypes = ["image/png"], FileExtensions = [".png"])] public UploadedFile? Photo { get; init; }

        [MaxLength(3), UploadLimits(MaxBytes = 2048)] public IReadOnlyList<UploadedFile> Receipts { get; init; } = [];

        public IReadOnlyList<UploadedFile> Scans { get; init; } = [];
    }

    private sealed record RegisterCustomerResponse(Guid Id);

    [JsonSerializable(typeof(CreatePaymentCommand))]
    [JsonSerializable(typeof(CreatePaymentResponse))]
    [JsonSerializable(typeof(GetPaymentResponse))]
    [JsonSerializable(typeof(RegisterCustomerCommand))]
    [JsonSerializable(typeof(RegisterCustomerResponse))]
    [JsonSerializable(typeof(SearchProductsResponse))]
    private sealed partial class OpenApiTestJsonContext : JsonSerializerContext;

    private sealed class CreatePaymentHandler : IHandler<CreatePaymentCommand, Result<CreatePaymentResponse>> {
        public ValueTask<Result<CreatePaymentResponse>>
            HandleAsync(CreatePaymentCommand request, CancellationToken ct) {
            return ValueTask.FromResult<Result<CreatePaymentResponse>>(new CreatePaymentResponse(Guid.NewGuid()));
        }
    }

    private sealed class GetPaymentHandler : IHandler<GetPaymentQuery, Result<GetPaymentResponse>> {
        public ValueTask<Result<GetPaymentResponse>> HandleAsync(GetPaymentQuery request, CancellationToken ct) {
            return ValueTask.FromResult<Result<GetPaymentResponse>>(new GetPaymentResponse(request.Id, "10.00"));
        }
    }

    private sealed class RegisterCustomerHandler : IHandler<RegisterCustomerCommand, Result<RegisterCustomerResponse>> {
        public ValueTask<Result<RegisterCustomerResponse>> HandleAsync(RegisterCustomerCommand request,
            CancellationToken ct) {
            return ValueTask.FromResult<Result<RegisterCustomerResponse>>(new RegisterCustomerResponse(Guid.NewGuid()));
        }
    }

    /// <summary>OpenAPI shape for 'Sample.Payments.CreatePayment' — never invoked; ApiExplorer reads the endpoint description, return type, and the registration's parameter metadata from this method.</summary>
    private delegate Task<IResult> ApiShape_CreatePayment_Signature();

    private static Task<IResult> ApiShape_CreatePayment() {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the generated RequestDelegate.");
    }

    /// <summary>OpenAPI shape for 'Sample.Payments.GetPayment' — never invoked; ApiExplorer reads the endpoint description, return type, and the registration's parameter metadata from this method.</summary>
    private delegate Task<IResult> ApiShape_GetPayment_Signature([FromRoute(Name = "Id")] Guid id);

    private static Task<IResult> ApiShape_GetPayment([FromRoute(Name = "Id")] Guid id) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the generated RequestDelegate.");
    }

    /// <summary>OpenAPI shape for 'Sample.Customers.RegisterCustomer' — never invoked; ApiExplorer reads the endpoint description, return type, and the registration's parameter metadata from this method.</summary>
    private delegate Task<IResult> ApiShape_RegisterCustomer_Signature();

    private static Task<IResult> ApiShape_RegisterCustomer() {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the generated RequestDelegate.");
    }

    [Fact]
    public async Task GeneratedEndpoints_ProduceElarionOpenApiDocument() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // Reflection stays OFF (no EnableReflectionFallback): the DTOs resolve only through this source-gen context.
        builder.Services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(OpenApiTestJsonContext.Default));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionOpenApi();
        builder.Services
            .AddScoped<IHandler<CreatePaymentCommand, Result<CreatePaymentResponse>>, CreatePaymentHandler>();
        builder.Services.AddScoped<IHandler<GetPaymentQuery, Result<GetPaymentResponse>>, GetPaymentHandler>();

        await using var app = builder.Build();

        // Mirrors the registrations AppModuleDiscoveryGenerator emits, including the OpenAPI-relevant metadata:
        // a module tag on both, the idempotency marker on the [Idempotent] POST only, AcceptsMetadata for the
        // JSON body / ElarionHttpParameterBindingMetadata per bound member, and the shape MethodInfo last so
        // ApiExplorer describes the endpoint (ADR-0071).
        var __shape0 = ((ApiShape_CreatePayment_Signature)ApiShape_CreatePayment).Method;
        var __bodyTypeInfo0 = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<CreatePaymentCommand>(app);
        app.MapPost("/payments",
                (RequestDelegate)(async __context => {
                    var __bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(__context, __bodyTypeInfo0);
                    if (__bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                        await ElarionHttpEndpointBinder.WriteBodyProblemAsync(__context, __bodyResult.Failure);
                        return;
                    }
                    var __request = __bodyResult.Value!;
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<CreatePaymentCommand, Result<CreatePaymentResponse>>>();
                    var __result = ElarionHttpResults.ToResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Payments.CreatePayment")
            .WithTags("Payments")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(CreatePaymentResponse),
                new[] { "application/json" }))
            .ProducesElarionErrors()
            .WithMetadata(ElarionIdempotentEndpointMetadata.Instance)
            .WithMetadata(new AcceptsMetadata(new[] { "application/json" }, typeof(CreatePaymentCommand), false))
            .WithMetadata(__shape0);

        var __shape1 = ((ApiShape_GetPayment_Signature)ApiShape_GetPayment).Method;
        var __shapeParameters1 = __shape1.GetParameters();
        app.MapGet("/payments/{id}",
                (RequestDelegate)(static async __context => {
                    var __errors = default(ElarionHttpBindingErrors);
                    var @id = ElarionHttpEndpointBinder.RouteValue<Guid>(__context, "Id", required: true,
                        ref __errors);
                    if (__errors.HasErrors) {
                        await __errors.WriteAsync(__context);
                        return;
                    }
                    var __request = new GetPaymentQuery {
                        Id = @id.GetValueOrDefault()
                    };
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<GetPaymentQuery, Result<GetPaymentResponse>>>();
                    var __result = ElarionHttpResults.ToResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Payments.GetPayment")
            .WithTags("Payments")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(GetPaymentResponse),
                new[] { "application/json" }))
            .ProducesElarionErrors()
            .WithMetadata(new ElarionHttpParameterBindingMetadata("Id", __shapeParameters1[0],
                hasTryParse: true, isOptional: false))
            .WithMetadata(__shape1);

        app.MapOpenApi();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync("/openapi/v1.json", ct);
            // (a) The document generates without throwing (which reflection-off schema failures would do → 500).
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var post = root.GetProperty("paths").GetProperty("/payments").GetProperty("post");
            var get = root.GetProperty("paths").GetProperty("/payments/{id}").GetProperty("get");

            // (c) Module tag flows through from the generator's .WithTags.
            post.GetProperty("tags")[0].GetString().Should().Be("Payments");
            get.GetProperty("tags")[0].GetString().Should().Be("Payments");

            // (d) Operation ids are normalized (namespace + no suffix), so generated clients get clean method names.
            post.GetProperty("operationId").GetString().Should().Be("CreatePayment");
            get.GetProperty("operationId").GetString().Should().Be("GetPayment");

            // (e) ProblemDetails error responses are advertised.
            post.GetProperty("responses").TryGetProperty("404", out _).Should().BeTrue();
            post.GetProperty("responses").TryGetProperty("409", out _).Should().BeTrue();

            // (e2) …and their schemas document the error contract's `code`/`data` extension members (ADR-0080), so an
            // off-the-shelf client generator types `error.code` instead of leaving it an unknown extension.
            foreach (var status in new[] { "400", "404" }) {
                var properties = ResolveSchema(root, post.GetProperty("responses").GetProperty(status)
                        .GetProperty("content").GetProperty("application/problem+json").GetProperty("schema"))
                    .GetProperty("properties");
                properties.GetProperty("code").GetProperty("type").GetString().Should().Be("string");
                properties.TryGetProperty("data", out _).Should().BeTrue();
                properties.GetProperty("reference").GetProperty("type").GetString().Should().Be("string");
                properties.TryGetProperty("title", out _).Should().BeTrue();
            }

            // (b) The body type schema resolved through the source-gen context (reflection off) — proving the wiring.
            root.GetProperty("components").GetProperty("schemas")
                .TryGetProperty(nameof(CreatePaymentResponse), out _).Should().BeTrue();

            // (g) The route member reaches the document as a path parameter — carried by the
            // ElarionHttpParameterBindingMetadata entry pointing at the [FromRoute]-attributed shape parameter
            // (regression: it was silently lost when the shape MethodInfo alone rode the metadata).
            HasParameter(get, "path", "Id").Should().BeTrue();

            // (f) The idempotent POST advertises the Idempotency-Key header and the x-elarion-idempotent extension…
            HasParameter(post, "header", "Idempotency-Key").Should().BeTrue();
            post.TryGetProperty(ElarionOpenApiExtensionNames.Idempotent, out var flag).Should().BeTrue();
            flag.GetBoolean().Should().BeTrue();

            // …while the plain GET advertises neither.
            HasParameter(get, "header", "Idempotency-Key").Should().BeFalse();
            get.TryGetProperty(ElarionOpenApiExtensionNames.Idempotent, out _).Should().BeFalse();
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task DataAnnotationConstraints_FlowIntoServedOpenApiDocument() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // Reflection stays OFF: the annotated DTO resolves only through the source-gen context, so the constraint
        // keywords asserted below prove the DataAnnotations→schema mapping works without runtime reflection.
        builder.Services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(OpenApiTestJsonContext.Default));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionOpenApi();
        builder.Services
            .AddScoped<IHandler<RegisterCustomerCommand, Result<RegisterCustomerResponse>>, RegisterCustomerHandler>();

        await using var app = builder.Build();

        var __shape0 = ((ApiShape_RegisterCustomer_Signature)ApiShape_RegisterCustomer).Method;
        var __bodyTypeInfo0 = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<RegisterCustomerCommand>(app);
        app.MapPost("/customers",
                (RequestDelegate)(async __context => {
                    var __bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(__context, __bodyTypeInfo0);
                    if (__bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                        await ElarionHttpEndpointBinder.WriteBodyProblemAsync(__context, __bodyResult.Failure);
                        return;
                    }
                    var __request = __bodyResult.Value!;
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<RegisterCustomerCommand, Result<RegisterCustomerResponse>>>();
                    var __result = ElarionHttpResults.ToResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Customers.RegisterCustomer")
            .WithTags("Customers")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(RegisterCustomerResponse),
                new[] { "application/json" }))
            .ProducesElarionErrors()
            .WithMetadata(new AcceptsMetadata(new[] { "application/json" }, typeof(RegisterCustomerCommand), false))
            .WithMetadata(__shape0);

        app.MapOpenApi();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync("/openapi/v1.json", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var properties = doc.RootElement.GetProperty("components").GetProperty("schemas")
                .GetProperty(nameof(RegisterCustomerCommand)).GetProperty("properties");

            // Elarion's transformer: Microsoft's built-in mapping omits [EmailAddress]; format: "email" keeps the
            // OpenAPI document in agreement with the JSON-RPC schema exporter (ADR-0027).
            properties.GetProperty("email").GetProperty("format").GetString().Should().Be("email");

            // Microsoft's built-in DataAnnotations mapping under the repo's reflection-off source-gen JSON setup —
            // load-bearing: [StringLength] and [Range] reach the document with no Elarion transformer involved.
            var displayName = properties.GetProperty("displayName");
            displayName.GetProperty("minLength").GetInt32().Should().Be(3);
            displayName.GetProperty("maxLength").GetInt32().Should().Be(100);

            var age = properties.GetProperty("age");
            age.GetProperty("minimum").GetDecimal().Should().Be(1);
            age.GetProperty("maximum").GetDecimal().Should().Be(120);

            // An uploaded file is the staged blob's id on the wire, marked as the JSON-RPC schema marks it. The nullable
            // member is a oneOf of null and the (shared) UploadedFile schema.
            var passport = properties.GetProperty("passport");
            var uploaded = passport.TryGetProperty("oneOf", out var alternatives)
                ? alternatives.EnumerateArray().Select(a => ResolveSchema(doc.RootElement, a))
                    .Single(a => a.TryGetProperty("x-elarion-upload", out _))
                : ResolveSchema(doc.RootElement, passport);
            uploaded.GetProperty("type").GetString().Should().Be("string");
            uploaded.GetProperty("x-elarion-upload").GetBoolean().Should().BeTrue();
            // The shared schema carries no member's limits.
            uploaded.TryGetProperty("x-elarion-max-bytes", out _).Should().BeFalse();
            uploaded.TryGetProperty("x-elarion-content-types", out _).Should().BeFalse();
            uploaded.TryGetProperty("x-elarion-file-extensions", out _).Should().BeFalse();

            // Declared [UploadLimits] (ADR-0100), as rpc-schema.json carries them: the member keeps its own schema.
            var contract = properties.GetProperty("contract");
            contract.TryGetProperty("$ref", out _).Should().BeFalse();
            contract.GetProperty("type").GetString().Should().Be("string");
            contract.GetProperty("x-elarion-upload").GetBoolean().Should().BeTrue();
            contract.GetProperty("x-elarion-max-bytes").GetInt64().Should().Be(1024);
            contract.GetProperty("x-elarion-content-types").EnumerateArray().Select(static t => t.GetString())
                .Should().Equal("application/pdf");

            var photo = properties.GetProperty("photo").GetProperty("oneOf").EnumerateArray()
                .Single(static a => a.TryGetProperty("x-elarion-upload", out _));
            photo.TryGetProperty("$ref", out _).Should().BeFalse();
            photo.GetProperty("x-elarion-content-types").EnumerateArray().Select(static t => t.GetString())
                .Should().Equal("image/png");
            photo.GetProperty("x-elarion-file-extensions").EnumerateArray().Select(static t => t.GetString())
                .Should().Equal(".png");
            photo.TryGetProperty("x-elarion-max-bytes", out _).Should().BeFalse();

            // A list gets its items back (the element serializes through a converter), with the member's limits.
            var receipts = properties.GetProperty("receipts");
            receipts.GetProperty("maxItems").GetInt32().Should().Be(3);
            receipts.TryGetProperty("x-elarion-max-bytes", out _).Should().BeFalse();
            receipts.GetProperty("items").GetProperty("x-elarion-upload").GetBoolean().Should().BeTrue();
            receipts.GetProperty("items").GetProperty("x-elarion-max-bytes").GetInt64().Should().Be(2048);
            var scans = properties.GetProperty("scans").GetProperty("items");
            scans.GetProperty("type").GetString().Should().Be("string");
            scans.GetProperty("x-elarion-upload").GetBoolean().Should().BeTrue();
            scans.TryGetProperty("x-elarion-max-bytes", out _).Should().BeFalse();
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private sealed record SearchProductsResponse(int Count);

    /// <summary>OpenAPI shape for 'Sample.Catalog.SearchProducts' — never invoked; ApiExplorer reads the endpoint description, return type, and the registration's parameter metadata from this method.</summary>
    private delegate Task<IResult> ApiShape_SearchProducts_Signature(
        [FromQuery(Name = "Page")] [Range(1, 120)] int page);

    private static Task<IResult> ApiShape_SearchProducts([FromQuery(Name = "Page")] [Range(1, 120)] int page) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the generated RequestDelegate.");
    }

    [Fact]
    public async Task QueryParameterConstraints_FlowIntoServedOpenApiDocument() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(OpenApiTestJsonContext.Default));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionOpenApi();

        await using var app = builder.Build();

        // Mirrors the member-wise metadata chain AppModuleDiscoveryGenerator emits for an annotated query member
        // (issue #131 / ADR-0071): the generator copies the DTO member's [Range] onto the shape parameter, and
        // the binding metadata points at it. The delegate itself never executes during document generation, so a
        // stub keeps this test about the metadata chain (binding behavior is covered by the binder/mapping tests).
        var __shape0 = ((ApiShape_SearchProducts_Signature)ApiShape_SearchProducts).Method;
        var __shapeParameters0 = __shape0.GetParameters();
        app.MapGet("/products",
                (RequestDelegate)(static __context => Task.CompletedTask))
            .WithName("Sample.Catalog.SearchProducts")
            .WithTags("Catalog")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(SearchProductsResponse),
                new[] { "application/json" }))
            .ProducesElarionErrors()
            .WithMetadata(new ElarionHttpParameterBindingMetadata("Page", __shapeParameters0[0],
                hasTryParse: true, isOptional: false))
            .WithMetadata(__shape0);

        app.MapOpenApi();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync("/openapi/v1.json", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var parameters = doc.RootElement.GetProperty("paths").GetProperty("/products").GetProperty("get")
                .GetProperty("parameters");

            // The copied [Range] on the shape parameter reaches the served document as schema bounds —
            // the regression this guards: member-wise constraints were silently lost when only the classified
            // [From*] attributes rode the flattened shape parameters.
            var page = parameters.EnumerateArray()
                .Single(parameter => parameter.GetProperty("name").GetString() == "Page");
            page.GetProperty("in").GetString().Should().Be("query");
            var schema = page.GetProperty("schema");
            schema.GetProperty("minimum").GetDecimal().Should().Be(1);
            schema.GetProperty("maximum").GetDecimal().Should().Be(120);
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private sealed record ExportQuery {
        public required string Kind { get; init; }
    }

    private sealed class ExportHandler : IHandler<ExportQuery, Result<ElarionFile>> {
        public ValueTask<Result<ElarionFile>> HandleAsync(ExportQuery request, CancellationToken ct) {
            return ValueTask.FromResult<Result<ElarionFile>>(
                new ElarionFile("id;name"u8.ToArray(), "text/csv") { FileName = "export.csv" });
        }
    }

    /// <summary>OpenAPI shape for 'Sample.Exports.GetExport' — never invoked; ApiExplorer reads the endpoint description, return type, and the registration's parameter metadata from this method.</summary>
    private delegate Task<IResult> ApiShape_GetExport_Signature([FromRoute(Name = "Kind")] string kind);

    private static Task<IResult> ApiShape_GetExport([FromRoute(Name = "Kind")] string kind) {
        throw new NotSupportedException("OpenAPI metadata shape only; requests execute through the generated RequestDelegate.");
    }

    [Fact]
    public async Task FileEndpoint_AdvertisesBinaryResponseInOpenApiDocument() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(OpenApiTestJsonContext.Default));
        builder.Services.AddProblemDetails();
        builder.Services.AddElarionOpenApi();
        builder.Services.AddScoped<IHandler<ExportQuery, Result<ElarionFile>>, ExportHandler>();

        await using var app = builder.Build();

        // Mirrors the file-endpoint registration and metadata AppModuleDiscoveryGenerator emits for a
        // Result<ElarionFile> handler.
        var __shape0 = ((ApiShape_GetExport_Signature)ApiShape_GetExport).Method;
        var __shapeParameters0 = __shape0.GetParameters();
        app.MapGet("/exports/{kind}",
                (RequestDelegate)(static async __context => {
                    var __errors = default(ElarionHttpBindingErrors);
                    var @kind = ElarionHttpEndpointBinder.RouteString(__context, "Kind", required: true,
                        ref __errors);
                    if (__errors.HasErrors) {
                        await __errors.WriteAsync(__context);
                        return;
                    }
                    var __request = new ExportQuery {
                        Kind = @kind!
                    };
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<ExportQuery, Result<ElarionFile>>>();
                    var __result = ElarionHttpResults.ToFileResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Exports.GetExport")
            .WithTags("Exports")
            .WithMetadata(new ProducesResponseTypeMetadata(200, null, new[] { "application/octet-stream" }))
            .ProducesElarionErrors()
            .WithMetadata(ElarionFileEndpointMetadata.Instance)
            .WithMetadata(new ElarionHttpParameterBindingMetadata("Kind", __shapeParameters0[0],
                hasTryParse: false, isOptional: false))
            .WithMetadata(__shape0);

        app.MapOpenApi();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync("/openapi/v1.json", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // The file transformer upgrades the marked operation's 200 response into an explicit binary payload,
            // so off-the-shelf client generators produce a blob/stream return instead of an empty object.
            var schema = doc.RootElement.GetProperty("paths").GetProperty("/exports/{kind}").GetProperty("get")
                .GetProperty("responses").GetProperty("200")
                .GetProperty("content").GetProperty("application/octet-stream")
                .GetProperty("schema");
            schema.GetProperty("type").GetString().Should().Be("string");
            schema.GetProperty("format").GetString().Should().Be("binary");
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private static JsonElement ResolveSchema(JsonElement document, JsonElement schema) {
        if (!schema.TryGetProperty("$ref", out var reference)) return schema;

        var name = reference.GetString()!["#/components/schemas/".Length..];
        return document.GetProperty("components").GetProperty("schemas").GetProperty(name);
    }

    private static bool HasParameter(JsonElement operation, string location, string name) {
        if (!operation.TryGetProperty("parameters", out var parameters)) return false;

        foreach (var parameter in parameters.EnumerateArray())
            if (parameter.TryGetProperty("in", out var parameterLocation) &&
                string.Equals(parameterLocation.GetString(), location, StringComparison.OrdinalIgnoreCase) &&
                parameter.TryGetProperty("name", out var parameterName) &&
                string.Equals(parameterName.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
