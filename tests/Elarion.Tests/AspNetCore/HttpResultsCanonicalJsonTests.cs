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
/// Regression for M20: a <c>[HttpEndpoint]</c> REST response must serialize through the canonical
/// <see cref="IElarionJsonSerialization"/> options — the same options JSON-RPC/MCP use — so REST output cannot
/// diverge from those transports for the same DTO. Here the canonical options apply an UPPERCASE naming policy
/// that ASP.NET's default HTTP JSON options do not, proving the success path went through the accessor.
/// </summary>
public sealed class HttpResultsCanonicalJsonTests {
    private sealed record Widget(string WidgetName);

    private sealed class WidgetHandler : IHandler<WidgetQuery, Result<Widget>> {
        public ValueTask<Result<Widget>> HandleAsync(WidgetQuery request, CancellationToken ct) =>
            ValueTask.FromResult<Result<Widget>>(new Widget("gadget"));
    }

    private sealed record WidgetQuery {
        public string? Ignored { get; init; }
    }

    private sealed class UpperCaseNamingPolicy : JsonNamingPolicy {
        public override string ConvertName(string name) => name.ToUpperInvariant();
    }

    [Fact]
    public async Task ToResult_SerializesThroughCanonicalElarionJsonOptions() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // ASP.NET's own HTTP JSON options stay at the camelCase default; the canonical Elarion options use an
        // UPPERCASE policy. If REST went through ASP.NET's options the property would be "widgetName".
        builder.Services.AddElarionJson();
        builder.Services.ConfigureElarionJson(o => {
            o.PropertyNamingPolicy = new UpperCaseNamingPolicy();
            o.EnableReflectionFallback = true;
        });
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
        } finally {
            await app.StopAsync(ct);
        }
    }
}
