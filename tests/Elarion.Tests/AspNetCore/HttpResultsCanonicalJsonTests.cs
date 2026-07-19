using System.Net;
using System.Text.Json;
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
/// Regression for M20: a <c>[HttpEndpoint]</c> REST response must serialize through Elarion's canonical JSON
/// configuration — the same the JSON-RPC/MCP transports use — so REST output cannot diverge for the same DTO.
/// The success value is written with <c>TypedResults.Ok</c>, so it flows through the HTTP JSON options that
/// <c>AddElarionHttpJson</c> aligns to canonical. Here the canonical options apply an UPPERCASE naming policy that
/// ASP.NET's default HTTP JSON options do not, proving the response went through the aligned canonical config.
/// </summary>
public sealed class HttpResultsCanonicalJsonTests {
    private sealed record Widget(string WidgetName);

    private sealed class WidgetHandler : IHandler<WidgetQuery, Result<Widget>> {
        public ValueTask<Result<Widget>> HandleAsync(WidgetQuery request, CancellationToken ct) {
            return ValueTask.FromResult<Result<Widget>>(new Widget("gadget"));
        }
    }

    private sealed record WidgetQuery {
        public string? Ignored { get; init; }
    }

    private sealed class UpperCaseNamingPolicy : JsonNamingPolicy {
        public override string ConvertName(string name) {
            return name.ToUpperInvariant();
        }
    }

    [Fact]
    public async Task ToResult_SerializesThroughCanonicalElarionJsonOptions() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // The canonical Elarion options use an UPPERCASE policy; AddElarionHttpJson aligns the HTTP JSON options
        // to them. Without that alignment REST would serialize through ASP.NET's camelCase default ("widgetName").
        builder.Services.ConfigureElarionJson(o => {
            o.PropertyNamingPolicy = new UpperCaseNamingPolicy();
            o.EnableReflectionFallback = true;
        });
        builder.Services.AddElarionHttpJson();
        builder.Services.AddScoped<IHandler<WidgetQuery, Result<Widget>>, WidgetHandler>();

        await using var app = builder.Build();

        app.MapGet("/widget", static async (
            [AsParameters] WidgetQuery request,
            [FromServices] IHandler<WidgetQuery, Result<Widget>> handler,
            CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)));

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync("/widget", ct);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync(ct);
            body.Should().Contain("WIDGETNAME");
            body.Should().NotContain("widgetName");
        }
        finally {
            await app.StopAsync(ct);
        }
    }
}
