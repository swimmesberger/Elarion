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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore.OpenApi;

/// <summary>
/// End-to-end test that boots a real Kestrel host, maps endpoints exactly as <c>AppModuleDiscoveryGenerator</c>
/// emits them for the OpenAPI package (module <c>.WithTags</c> and the idempotent <c>.WithMetadata</c> marker),
/// registers <see cref="ElarionOpenApiServiceCollectionExtensions.AddElarionOpenApi(IServiceCollection, Action{Microsoft.AspNetCore.OpenApi.OpenApiOptions}?)"/>,
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
    }

    private sealed record RegisterCustomerResponse(Guid Id);

    [JsonSerializable(typeof(CreatePaymentCommand))]
    [JsonSerializable(typeof(CreatePaymentResponse))]
    [JsonSerializable(typeof(GetPaymentResponse))]
    [JsonSerializable(typeof(RegisterCustomerCommand))]
    [JsonSerializable(typeof(RegisterCustomerResponse))]
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

        // Mirrors the lambdas AppModuleDiscoveryGenerator emits, including the OpenAPI-relevant metadata: a module
        // tag on both, and the idempotency marker on the [Idempotent] POST only.
        app.MapPost("/payments", static async (
                CreatePaymentCommand request,
                [FromServices] IHandler<CreatePaymentCommand, Result<CreatePaymentResponse>> handler,
                CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)))
            .WithName("Sample.Payments.CreatePayment")
            .WithTags("Payments")
            .Produces<CreatePaymentResponse>(200)
            .ProducesElarionErrors()
            .WithMetadata(ElarionIdempotentEndpointMetadata.Instance);

        app.MapGet("/payments/{id}", static async (
                [AsParameters] GetPaymentQuery request,
                [FromServices] IHandler<GetPaymentQuery, Result<GetPaymentResponse>> handler,
                CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)))
            .WithName("Sample.Payments.GetPayment")
            .WithTags("Payments")
            .Produces<GetPaymentResponse>(200)
            .ProducesElarionErrors();

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

            // (b) The body type schema resolved through the source-gen context (reflection off) — proving the wiring.
            root.GetProperty("components").GetProperty("schemas")
                .TryGetProperty(nameof(CreatePaymentResponse), out _).Should().BeTrue();

            // (f) The idempotent POST advertises the Idempotency-Key header and the x-elarion-idempotent extension…
            HasHeaderParameter(post, "Idempotency-Key").Should().BeTrue();
            post.TryGetProperty(ElarionOpenApiExtensionNames.Idempotent, out var flag).Should().BeTrue();
            flag.GetBoolean().Should().BeTrue();

            // …while the plain GET advertises neither.
            HasHeaderParameter(get, "Idempotency-Key").Should().BeFalse();
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

        app.MapPost("/customers", static async (
                RegisterCustomerCommand request,
                [FromServices] IHandler<RegisterCustomerCommand, Result<RegisterCustomerResponse>> handler,
                CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)))
            .WithName("Sample.Customers.RegisterCustomer")
            .WithTags("Customers")
            .Produces<RegisterCustomerResponse>(200)
            .ProducesElarionErrors();

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

        // Mirrors the file-endpoint metadata AppModuleDiscoveryGenerator emits for a Result<ElarionFile> handler.
        app.MapGet("/exports/{kind}", static async (
                [AsParameters] ExportQuery request,
                [FromServices] IHandler<ExportQuery, Result<ElarionFile>> handler,
                CancellationToken token) => ElarionHttpResults.ToFileResult(await handler.HandleAsync(request, token)))
            .WithName("Sample.Exports.GetExport")
            .WithTags("Exports")
            .Produces(200, null, "application/octet-stream")
            .ProducesElarionErrors()
            .WithMetadata(ElarionFileEndpointMetadata.Instance);

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

    private static bool HasHeaderParameter(JsonElement operation, string name) {
        if (!operation.TryGetProperty("parameters", out var parameters)) return false;

        foreach (var parameter in parameters.EnumerateArray())
            if (parameter.TryGetProperty("in", out var location) &&
                string.Equals(location.GetString(), "header", StringComparison.OrdinalIgnoreCase) &&
                parameter.TryGetProperty("name", out var parameterName) &&
                string.Equals(parameterName.GetString(), name, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}
