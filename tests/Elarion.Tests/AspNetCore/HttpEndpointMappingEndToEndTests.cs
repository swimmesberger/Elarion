using System.Net;
using System.Text;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// End-to-end test that boots a real Kestrel host and maps endpoints exactly as
/// <c>AppModuleDiscoveryGenerator</c> emits them — proving the runtime contract the generator targets works over the
/// wire: <c>[AsParameters]</c> binding of a <c>required</c>-member query record, body binding, <c>[FromServices]</c>
/// handler resolution, and <see cref="AppError"/> → RFC 7807 ProblemDetails translation.
/// </summary>
public sealed class HttpEndpointMappingEndToEndTests {
    private static readonly Guid MissingId = new("00000000-0000-0000-0000-0000000000ff");

    private sealed record GetWidgetQuery {
        public required Guid Id { get; init; }
    }

    private sealed record WidgetResponse(Guid Id, string Name);

    private sealed class GetWidgetHandler : IHandler<GetWidgetQuery, Result<WidgetResponse>> {
        public ValueTask<Result<WidgetResponse>> HandleAsync(GetWidgetQuery request, CancellationToken ct) {
            return request.Id == MissingId
                ? ValueTask.FromResult<Result<WidgetResponse>>(AppError.NotFound("widget not found"))
                : ValueTask.FromResult<Result<WidgetResponse>>(new WidgetResponse(request.Id, "Widget"));
        }
    }

    private sealed record CreateWidgetCommand {
        public required string Name { get; init; }
    }

    private sealed record CreateWidgetResponse(Guid Id);

    private sealed class CreateWidgetHandler : IHandler<CreateWidgetCommand, Result<CreateWidgetResponse>> {
        public ValueTask<Result<CreateWidgetResponse>> HandleAsync(CreateWidgetCommand request, CancellationToken ct) {
            return string.IsNullOrWhiteSpace(request.Name)
                ? ValueTask.FromResult<Result<CreateWidgetResponse>>(AppError.Validation("invalid",
                    ["Name is required"]))
                : ValueTask.FromResult<Result<CreateWidgetResponse>>(new CreateWidgetResponse(Guid.NewGuid()));
        }
    }

    private sealed record ExportQuery {
        public required string Kind { get; init; }
    }

    private sealed class ExportHandler : IHandler<ExportQuery, Result<ElarionFile>> {
        public ValueTask<Result<ElarionFile>> HandleAsync(ExportQuery request, CancellationToken ct) {
            return request.Kind switch {
                "named" => ValueTask.FromResult<Result<ElarionFile>>(
                    new ElarionFile("id;name"u8.ToArray(), "text/csv") { FileName = "clients.csv" }),
                "inline" => ValueTask.FromResult<Result<ElarionFile>>(
                    new ElarionFile("inline-content"u8.ToArray(), "application/octet-stream")),
                _ => ValueTask.FromResult<Result<ElarionFile>>(AppError.NotFound("no such export"))
            };
        }
    }

    [Fact]
    public async Task GeneratedFileEndpointShape_WritesDownloadsAndMapsErrors() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddScoped<IHandler<ExportQuery, Result<ElarionFile>>, ExportHandler>();

        await using var app = builder.Build();

        // Mirrors the file-endpoint lambda emitted by AppModuleDiscoveryGenerator for a Result<ElarionFile> handler.
        app.MapGet("/exports/{kind}", static async (
            [AsParameters] ExportQuery request,
            [FromServices] IHandler<ExportQuery, Result<ElarionFile>> handler,
            CancellationToken token) => ElarionHttpResults.ToFileResult(await handler.HandleAsync(request, token)));

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var named = await client.GetAsync("/exports/named", ct);
            named.StatusCode.Should().Be(HttpStatusCode.OK);
            named.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
            named.Content.Headers.ContentDisposition!.ToString()
                .Should().Contain("attachment").And.Contain("clients.csv");
            (await named.Content.ReadAsStringAsync(ct)).Should().Be("id;name");

            // A payload without a file name is served inline (no Content-Disposition).
            var inline = await client.GetAsync("/exports/inline", ct);
            inline.StatusCode.Should().Be(HttpStatusCode.OK);
            inline.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            inline.Content.Headers.ContentDisposition.Should().BeNull();
            (await inline.Content.ReadAsStringAsync(ct)).Should().Be("inline-content");

            var missing = await client.GetAsync("/exports/none", ct);
            missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            missing.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            (await missing.Content.ReadAsStringAsync(ct)).Should().Contain("no such export");
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task GeneratedEndpointShape_BindsRequestsAndMapsErrors() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        // Real hosts get the canonical accessor via the generated AddElarion(configuration); success responses
        // serialize through it (never ASP.NET's own options). The reflection fallback stands in for the module
        // JSON context this test host doesn't generate.
        builder.Services.ConfigureElarionJson(o => o.EnableReflectionFallback = true);
        builder.Services.AddProblemDetails();
        builder.Services.AddScoped<IHandler<GetWidgetQuery, Result<WidgetResponse>>, GetWidgetHandler>();
        builder.Services.AddScoped<IHandler<CreateWidgetCommand, Result<CreateWidgetResponse>>, CreateWidgetHandler>();

        await using var app = builder.Build();

        // Mirrors the lambdas emitted by AppModuleDiscoveryGenerator.
        app.MapGet("/widgets/{id}", static async (
            [AsParameters] GetWidgetQuery request,
            [FromServices] IHandler<GetWidgetQuery, Result<WidgetResponse>> handler,
            CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)));

        app.MapPost("/widgets", static async (
            CreateWidgetCommand request,
            [FromServices] IHandler<CreateWidgetCommand, Result<CreateWidgetResponse>> handler,
            CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)));

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var id = Guid.NewGuid();
            var getOk = await client.GetAsync($"/widgets/{id}", ct);
            getOk.StatusCode.Should().Be(HttpStatusCode.OK);
            (await getOk.Content.ReadAsStringAsync(ct)).Should().Contain("Widget").And.Contain(id.ToString());

            var getMissing = await client.GetAsync($"/widgets/{MissingId}", ct);
            getMissing.StatusCode.Should().Be(HttpStatusCode.NotFound);
            getMissing.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            (await getMissing.Content.ReadAsStringAsync(ct)).Should().Contain("widget not found");

            var postOk = await client.PostAsync(
                "/widgets", new StringContent("""{"name":"Gadget"}""", Encoding.UTF8, "application/json"), ct);
            postOk.StatusCode.Should().Be(HttpStatusCode.OK);
            (await postOk.Content.ReadAsStringAsync(ct)).Should().Contain("id");

            var postInvalid = await client.PostAsync(
                "/widgets", new StringContent("""{"name":""}""", Encoding.UTF8, "application/json"), ct);
            postInvalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await postInvalid.Content.ReadAsStringAsync(ct)).Should().Contain("Name is required");
        }
        finally {
            await app.StopAsync(ct);
        }
    }
}
