using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// Covers <see cref="ElarionHttpEndpointBinder"/> over a real Kestrel host — the static binding helpers (with
/// by-<see langword="ref"/> <see cref="ElarionHttpBindingErrors"/> state and a mapping-time-resolved body
/// <c>JsonTypeInfo</c>) behind the AOT-safe <c>Map*(string, RequestDelegate)</c> registrations
/// <c>AppModuleDiscoveryGenerator</c> emits for <c>[HttpEndpoint]</c> handlers (issue #131 / ADR-0071):
/// generated endpoints must never fall back to ASP.NET Core's reflection-based RequestDelegateFactory, and
/// binding failures are RFC 7807 <c>ValidationProblem</c> responses keyed by the wire name the client sent
/// (or 415 for a wrong content type).
/// </summary>
public sealed class HttpEndpointBinderTests {
    private enum SortDirection {
        Ascending,
        Descending
    }

    private sealed record MetricsQuery {
        public required string Metric { get; init; }
        public int Size { get; init; } = 25;
        public string? After { get; init; }
    }

    private sealed record CreateOrderCommand {
        public required string Name { get; init; }
    }

    private static void MapItemEndpoint(WebApplication app) {
        app.MapGet("/items/{id}", (RequestDelegate)(static async context => {
            var errors = default(ElarionHttpBindingErrors);
            var id = ElarionHttpEndpointBinder.RouteValue<Guid>(context, "Id", required: true, ref errors);
            if (errors.HasErrors) {
                await errors.WriteAsync(context);
                return;
            }
            await context.Response.WriteAsync($"item:{id.GetValueOrDefault()}");
        }));
    }

    private static void MapMetricsEndpoint(WebApplication app) {
        app.MapGet("/metrics", (RequestDelegate)(static async context => {
            var errors = default(ElarionHttpBindingErrors);
            var metric = ElarionHttpEndpointBinder.QueryString(context, "Metric", required: true, ref errors);
            var size = ElarionHttpEndpointBinder.QueryValue<int>(context, "Size", required: false, ref errors);
            var after = ElarionHttpEndpointBinder.QueryString(context, "After", required: false, ref errors);
            if (errors.HasErrors) {
                await errors.WriteAsync(context);
                return;
            }
            // Mirrors the generator's defaults probe: a member the wire did not set keeps the DTO's own default.
            var defaults = new MetricsQuery { Metric = metric! };
            var request = new MetricsQuery {
                Metric = metric!,
                Size = size ?? defaults.Size,
                After = after
            };
            await context.Response.WriteAsync($"{request.Metric}|{request.Size}|{request.After ?? "<null>"}");
        }));
    }

    private static void MapSortedEndpoint(WebApplication app) {
        app.MapGet("/sorted", (RequestDelegate)(static async context => {
            var errors = default(ElarionHttpBindingErrors);
            var direction = ElarionHttpEndpointBinder.QueryEnum<SortDirection>(context, "Direction",
                required: true, ref errors);
            if (errors.HasErrors) {
                await errors.WriteAsync(context);
                return;
            }
            await context.Response.WriteAsync(direction.GetValueOrDefault().ToString());
        }));
    }

    private static void MapIdsEndpoint(WebApplication app) {
        app.MapGet("/ids", (RequestDelegate)(static async context => {
            var errors = default(ElarionHttpBindingErrors);
            var values = ElarionHttpEndpointBinder.QueryValues<int>(context, "N", required: true, ref errors);
            if (errors.HasErrors) {
                await errors.WriteAsync(context);
                return;
            }
            await context.Response.WriteAsync(string.Join(",", values!));
        }));
    }

    private static void MapOrderEndpoint(WebApplication app) {
        // The body JsonTypeInfo resolves once at mapping time, exactly as the generated registration does; the
        // delegate captures it (non-static).
        var bodyTypeInfo = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<CreateOrderCommand>(app);
        app.MapPost("/orders", (RequestDelegate)(async context => {
            var bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(context, bodyTypeInfo);
            if (bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                await ElarionHttpEndpointBinder.WriteBodyProblemAsync(context, bodyResult.Failure);
                return;
            }
            var request = bodyResult.Value!;
            await context.Response.WriteAsync($"order:{request.Name}");
        }));
    }

    [Fact]
    public async Task RouteValue_BindsGuidFromRouteToken() {
        await RunHostAsync(MapItemEndpoint, static async (client, ct) => {
            var id = Guid.NewGuid();
            var response = await client.GetAsync($"/items/{id}", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be($"item:{id}");
        });
    }

    [Fact]
    public async Task RouteValue_ParseFailure_Returns400KeyedByLookupName() {
        await RunHostAsync(MapItemEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/items/not-a-guid", ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var errors = await ReadProblemErrorsAsync(response, ct);
            errors.GetProperty("Id")[0].GetString().Should().Be("The value 'not-a-guid' is not valid for Id.");
        });
    }

    [Fact]
    public async Task QueryString_RequiredMissing_Returns400WithRequiredMessage() {
        await RunHostAsync(MapMetricsEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/metrics", ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var errors = await ReadProblemErrorsAsync(response, ct);
            errors.GetProperty("Metric")[0].GetString().Should().Be("The Metric field is required.");
        });
    }

    [Fact]
    public async Task QueryBinding_OptionalValuesAbsent_KeepPropertyDefaults() {
        await RunHostAsync(MapMetricsEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/metrics?Metric=cpu", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be("cpu|25|<null>");
        });
    }

    [Fact]
    public async Task QueryBinding_OptionalValuesPresent_OverridePropertyDefaults() {
        await RunHostAsync(MapMetricsEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/metrics?Metric=cpu&Size=50&After=abc", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be("cpu|50|abc");
        });
    }

    [Fact]
    public async Task QueryEnum_ParsesCaseInsensitively() {
        await RunHostAsync(MapSortedEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/sorted?Direction=descending", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be("Descending");
        });
    }

    [Fact]
    public async Task QueryEnum_InvalidValue_Returns400ValidationProblem() {
        await RunHostAsync(MapSortedEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/sorted?Direction=sideways", ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var errors = await ReadProblemErrorsAsync(response, ct);
            errors.GetProperty("Direction")[0].GetString()
                .Should().Be("The value 'sideways' is not valid for Direction.");
        });
    }

    [Fact]
    public async Task QueryValues_BindsRepeatedKeyAsArray() {
        await RunHostAsync(MapIdsEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/ids?N=1&N=2&N=3", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be("1,2,3");
        });
    }

    [Fact]
    public async Task QueryValues_InvalidElement_Returns400ValidationProblem() {
        await RunHostAsync(MapIdsEndpoint, static async (client, ct) => {
            var response = await client.GetAsync("/ids?N=1&N=two", ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var errors = await ReadProblemErrorsAsync(response, ct);
            errors.GetProperty("N")[0].GetString().Should().Be("The value 'two' is not valid for N.");
        });
    }

    [Fact]
    public async Task ReadJsonBody_BindsJsonRequestBody() {
        await RunHostAsync(MapOrderEndpoint, static async (client, ct) => {
            var response = await client.PostAsync(
                "/orders", new StringContent("""{"name":"Widget"}""", Encoding.UTF8, "application/json"), ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be("order:Widget");
        });
    }

    [Fact]
    public async Task ReadJsonBody_WrongContentType_Returns415() {
        await RunHostAsync(MapOrderEndpoint, static async (client, ct) => {
            var response = await client.PostAsync(
                "/orders", new StringContent("""{"name":"Widget"}""", Encoding.UTF8, "text/plain"), ct);
            response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        });
    }

    [Fact]
    public async Task ReadJsonBody_NullOrEmptyBody_Returns400WithNonFieldKey() {
        await RunHostAsync(MapOrderEndpoint, static async (client, ct) => {
            var nullBody = await client.PostAsync(
                "/orders", new StringContent("null", Encoding.UTF8, "application/json"), ct);
            nullBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var nullErrors = await ReadProblemErrorsAsync(nullBody, ct);
            nullErrors.GetProperty("")[0].GetString().Should().Be("A non-empty request body is required.");

            var emptyBody = await client.PostAsync(
                "/orders", new StringContent(string.Empty, Encoding.UTF8, "application/json"), ct);
            emptyBody.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var emptyErrors = await ReadProblemErrorsAsync(emptyBody, ct);
            emptyErrors.TryGetProperty("", out _).Should().BeTrue();
        });
    }

    [Fact]
    public async Task ReadJsonBody_MalformedJson_Returns400WithNonFieldKey() {
        await RunHostAsync(MapOrderEndpoint, static async (client, ct) => {
            var response = await client.PostAsync(
                "/orders", new StringContent("""{"name":""", Encoding.UTF8, "application/json"), ct);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var errors = await ReadProblemErrorsAsync(response, ct);
            errors.GetProperty("")[0].GetString().Should().Be("The request body could not be read as valid JSON.");
        });
    }

    private static async Task RunHostAsync(
        Action<WebApplication> map, Func<HttpClient, CancellationToken, Task> assert) {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        // The reflection resolver stands in for the source-generated module context real hosts wire through
        // AddElarionHttpJson; the binder reads JSON bodies through these minimal-API options either way.
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());

        await using var app = builder.Build();
        map(app);
        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
            await assert(client, ct);
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private static async Task<JsonElement> ReadProblemErrorsAsync(HttpResponseMessage response, CancellationToken ct) {
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("errors").Clone();
    }
}
