using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// Covers <see cref="ElarionHttpJsonServiceCollectionExtensions.AddElarionHttpJson"/>: the base HTTP-transport
/// wiring that lets a <c>[HttpEndpoint]</c> request body deserialize through the canonical source-generated
/// contexts with reflection off (the repo/AOT default), independent of OpenAPI. Also proves a host's later
/// <c>ConfigureHttpJsonOptions</c> composes on top of — and can override — the Elarion alignment.
/// </summary>
public sealed partial class ElarionHttpJsonEndToEndTests {
    private sealed record CreateThingCommand {
        public required string Name { get; init; }
    }

    private sealed record CreateThingResponse(string Name);

    [JsonSerializable(typeof(CreateThingCommand))]
    [JsonSerializable(typeof(CreateThingResponse))]
    private sealed partial class HttpJsonTestContext : JsonSerializerContext;

    private sealed class CreateThingHandler : IHandler<CreateThingCommand, Result<CreateThingResponse>> {
        public ValueTask<Result<CreateThingResponse>> HandleAsync(CreateThingCommand request, CancellationToken ct) =>
            ValueTask.FromResult<Result<CreateThingResponse>>(new CreateThingResponse(request.Name));
    }

    [Fact]
    public async Task AddElarionHttpJson_BindsPostBody_WithReflectionOff() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        // Reflection stays OFF — no EnableReflectionFallback. The command type resolves only via this source-gen
        // context, and only because AddElarionHttpJson copies the canonical resolver onto the HTTP JSON options.
        builder.Services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(HttpJsonTestContext.Default));
        builder.Services.AddElarionHttpJson();
        builder.Services.AddProblemDetails();
        builder.Services.AddScoped<IHandler<CreateThingCommand, Result<CreateThingResponse>>, CreateThingHandler>();

        await using var app = builder.Build();

        app.MapPost("/things", static async (
            CreateThingCommand request,
            [FromServices] IHandler<CreateThingCommand, Result<CreateThingResponse>> handler,
            CancellationToken token) => ElarionHttpResults.ToResult(await handler.HandleAsync(request, token)));

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.PostAsync(
                "/things", new StringContent("""{"name":"Widget"}""", Encoding.UTF8, "application/json"), ct);

            // 200 (not 500) proves the body deserialized through the source-gen resolver with reflection off.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync(ct)).Should().Contain("Widget");
        } finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public void HostConfigureHttpJsonOptions_After_OverridesElarion() {
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Add(HttpJsonTestContext.Default));

        // Elarion aligns first (canonical PropertyNamingPolicy = camelCase)…
        services.AddElarionHttpJson();
        // …then the host overrides afterwards. Registration order means the host's Configure runs last and wins.
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HttpJsonOptions>>().Value;

        options.SerializerOptions.PropertyNamingPolicy.Should().BeNull();
        // The Elarion resolver is still present — the override composes on top, it does not discard the alignment.
        var elarion = provider.GetRequiredService<IElarionJsonSerialization>().Options.TypeInfoResolverChain[0];
        options.SerializerOptions.TypeInfoResolverChain.Should().Contain(elarion);
    }
}
