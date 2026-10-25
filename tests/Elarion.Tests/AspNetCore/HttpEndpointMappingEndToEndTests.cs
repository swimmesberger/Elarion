using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// End-to-end test that boots a real Kestrel host and maps endpoints exactly as
/// <c>AppModuleDiscoveryGenerator</c> emits them (issue #131 / ADR-0071) — proving the runtime contract the
/// generator targets works over the wire: an AOT-safe <c>Map*(string, RequestDelegate)</c> registration whose
/// delegate binds through <see cref="ElarionHttpEndpointBinder"/> (never RequestDelegateFactory), typed handler
/// resolution from <c>RequestServices</c>, <see cref="AppError"/> → RFC 7807 ProblemDetails translation, and the
/// binder's own RFC 7807 binding-failure tier (400 ValidationProblem / 415).
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

    private sealed record CreateGadgetCommand {
        public required string Name { get; init; }
    }

    private sealed record CreateGadgetResponse(Guid Id, string Name);

    private sealed class CreateGadgetHandler
        : IHandler<CreateGadgetCommand, Result<ElarionCreated<CreateGadgetResponse>>> {
        public static readonly Guid CreatedId = new("00000000-0000-0000-0000-000000000042");

        public ValueTask<Result<ElarionCreated<CreateGadgetResponse>>> HandleAsync(
            CreateGadgetCommand request, CancellationToken ct) {
            return request.Name switch {
                "" => ValueTask.FromResult<Result<ElarionCreated<CreateGadgetResponse>>>(
                    AppError.Conflict("gadget already exists")),
                "unlocated" => ValueTask.FromResult<Result<ElarionCreated<CreateGadgetResponse>>>(
                    new ElarionCreated<CreateGadgetResponse>(new CreateGadgetResponse(CreatedId, request.Name))),
                _ => ValueTask.FromResult<Result<ElarionCreated<CreateGadgetResponse>>>(
                    new ElarionCreated<CreateGadgetResponse>(new CreateGadgetResponse(CreatedId, request.Name)) {
                        Location = $"/gadgets/{CreatedId}",
                    })
            };
        }
    }

    [Fact]
    public async Task GeneratedCreatedEndpointShape_Writes201WithLocationAndMapsErrors() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.ConfigureElarionJson(o => o.EnableReflectionFallback = true);
        builder.Services.AddProblemDetails();
        builder.Services
            .AddScoped<IHandler<CreateGadgetCommand, Result<ElarionCreated<CreateGadgetResponse>>>,
                CreateGadgetHandler>();

        await using var app = builder.Build();

        // Mirrors the created-endpoint registration emitted by AppModuleDiscoveryGenerator for a
        // Result<ElarionCreated<T>> handler: the envelope is peeled by ToCreatedResult, so the wire carries the
        // inner value with 201 and the optional Location header.
        var __bodyTypeInfo0 = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<CreateGadgetCommand>(app);
        app.MapPost("/gadgets",
                (RequestDelegate)(async __context => {
                    var __bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(__context, __bodyTypeInfo0);
                    if (__bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                        await ElarionHttpEndpointBinder.WriteBodyProblemAsync(__context, __bodyResult.Failure);
                        return;
                    }
                    var __request = __bodyResult.Value!;
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<CreateGadgetCommand, Result<ElarionCreated<CreateGadgetResponse>>>>();
                    var __result = ElarionHttpResults.ToCreatedResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Gadgets.CreateGadget")
            .WithMetadata(new ProducesResponseTypeMetadata(201, typeof(CreateGadgetResponse),
                new[] { "application/json" }))
            .ProducesElarionErrors();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var created = await client.PostAsync(
                "/gadgets", new StringContent("""{"name":"Gadget"}""", Encoding.UTF8, "application/json"), ct);
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            created.Headers.Location!.ToString().Should().Be($"/gadgets/{CreateGadgetHandler.CreatedId}");
            var body = await created.Content.ReadAsStringAsync(ct);
            body.Should().Contain("Gadget").And.Contain(CreateGadgetHandler.CreatedId.ToString());
            body.Should().NotContain("location");

            // A null Location is a 201 without the header — not an error.
            var unlocated = await client.PostAsync(
                "/gadgets", new StringContent("""{"name":"unlocated"}""", Encoding.UTF8, "application/json"), ct);
            unlocated.StatusCode.Should().Be(HttpStatusCode.Created);
            unlocated.Headers.Location.Should().BeNull();

            // Failures keep the central AppError -> RFC 7807 translation.
            var conflict = await client.PostAsync(
                "/gadgets", new StringContent("""{"name":""}""", Encoding.UTF8, "application/json"), ct);
            conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
            conflict.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            (await conflict.Content.ReadAsStringAsync(ct)).Should().Contain("gadget already exists");
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task GeneratedEndpointGroup_KeepsAllowAnonymousRoutesOpenUnderHostAuthorization() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.ConfigureElarionJson(o => o.EnableReflectionFallback = true);
        builder.Services.AddProblemDetails();
        builder.Services.AddAuthentication(AnonymousOnlyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, AnonymousOnlyAuthenticationHandler>(
                AnonymousOnlyAuthenticationHandler.SchemeName, null);
        // The deny-by-default posture the returned group exists for: a hand-mapped route that forgets
        // RequireAuthorization() is closed too.
        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.Services.AddScoped<IHandler<GetWidgetQuery, Result<WidgetResponse>>, GetWidgetHandler>();

        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();

        // Mirrors MapElarionEndpoints: every route maps into one prefix-less group, which the host then guards; the
        // [AllowAnonymous] handler's route carries .AllowAnonymous() and stays open.
        var elarionEndpoints = app.MapGroup(string.Empty);
        foreach (var (route, isAnonymous) in new[] { ("/public/widgets/{id}", true), ("/widgets/{id}", false) }) {
            var endpoint = elarionEndpoints.MapGet(route,
                    (RequestDelegate)(static async __context => {
                        var __request = new GetWidgetQuery {
                            Id = Guid.Parse((string)__context.Request.RouteValues["id"]!)
                        };
                        var __handler = __context.RequestServices
                            .GetRequiredService<IHandler<GetWidgetQuery, Result<WidgetResponse>>>();
                        var __result = ElarionHttpResults.ToResult(
                            await __handler.HandleAsync(__request, __context.RequestAborted));
                        await __result.ExecuteAsync(__context);
                    }))
                .ProducesElarionErrors();
            if (isAnonymous) endpoint.AllowAnonymous();
        }

        elarionEndpoints.RequireAuthorization();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };
            var id = Guid.CreateVersion7();

            (await client.GetAsync($"/public/widgets/{id}", ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync($"/widgets/{id}", ct)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    private sealed class ThrowingWidgetHandler : IHandler<GetWidgetQuery, Result<WidgetResponse>> {
        public ValueTask<Result<WidgetResponse>> HandleAsync(GetWidgetQuery request, CancellationToken ct) {
            throw new InvalidOperationException("connection string leaked");
        }
    }

    [Fact]
    public async Task GeneratedEndpointShape_AnswersUnexpectedExceptionsWithTheConfiguredProblem() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureElarionUnexpectedErrors(o => {
            o.Message = context => $"Unexpected failure in {context.Operation} ({context.Transport}).";
            o.Reference = _ => "REF-9";
        });
        builder.Services.AddScoped<IHandler<GetWidgetQuery, Result<WidgetResponse>>, ThrowingWidgetHandler>();

        await using var app = builder.Build();

        // Mirrors the generated delegate's outermost catch around handler resolution and the whole pipeline.
        app.MapGet("/widgets/{id}",
                (RequestDelegate)(static async __context => {
                    var __request = new GetWidgetQuery {
                        Id = Guid.Parse((string)__context.Request.RouteValues["id"]!)
                    };
                    IResult __result;
                    try {
                        var __handler = __context.RequestServices
                            .GetRequiredService<IHandler<GetWidgetQuery, Result<WidgetResponse>>>();
                        __result = ElarionHttpResults.ToResult(
                            await __handler.HandleAsync(__request, __context.RequestAborted));
                    }
                    catch (Exception __exception) when (ElarionHttpResults.IsUnexpected(__context, __exception)) {
                        __result = ElarionHttpResults.ToUnexpectedProblem(__context, __exception, "Sample.Widgets.GetWidget");
                    }
                    await __result.ExecuteAsync(__context);
                }))
            .ProducesElarionErrors();

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync($"/widgets/{Guid.CreateVersion7()}", ct);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            var body = await response.Content.ReadAsStringAsync(ct);
            using var problem = JsonDocument.Parse(body);
            problem.RootElement.GetProperty("detail").GetString()
                .Should().Be("Unexpected failure in Sample.Widgets.GetWidget (http).");
            problem.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.Internal);
            problem.RootElement.GetProperty("reference").GetString().Should().Be("REF-9");
            body.Should().NotContain("connection string leaked");
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task GeneratedEndpointShape_LeavesAnExceptionToTheHostWhenTheOptionsSaySo() {
        var ct = TestContext.Current.CancellationToken;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureElarionUnexpectedErrors(o =>
            o.LeaveToHost = context => context.Exception is InvalidOperationException);
        builder.Services.AddScoped<IHandler<GetWidgetQuery, Result<WidgetResponse>>, ThrowingWidgetHandler>();

        await using var app = builder.Build();
        // The host maps the exception to its own status, as an IExceptionHandler mapping a concurrency conflict would.
        app.UseExceptionHandler(handler => handler.Run(static async context => {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsync("mapped by the host");
        }));
        app.MapGet("/widgets/{id}",
            (RequestDelegate)(static async __context => {
                var __request = new GetWidgetQuery { Id = Guid.Parse((string)__context.Request.RouteValues["id"]!) };
                IResult __result;
                try {
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<GetWidgetQuery, Result<WidgetResponse>>>();
                    __result = ElarionHttpResults.ToResult(await __handler.HandleAsync(__request, __context.RequestAborted));
                }
                catch (Exception __exception) when (ElarionHttpResults.IsUnexpected(__context, __exception)) {
                    __result = ElarionHttpResults.ToUnexpectedProblem(__context, __exception, "Sample.Widgets.GetWidget");
                }
                await __result.ExecuteAsync(__context);
            }));

        await app.StartAsync(ct);

        try {
            var baseAddress = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(baseAddress) };

            var response = await client.GetAsync($"/widgets/{Guid.CreateVersion7()}", ct);

            response.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await response.Content.ReadAsStringAsync(ct)).Should().Be("mapped by the host");
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    // Authenticates nobody: every request is anonymous, so only routes carrying IAllowAnonymous pass a policy that
    // requires an authenticated user.
    private sealed class AnonymousOnlyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {
        public const string SchemeName = "anonymous-only";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() {
            return Task.FromResult(AuthenticateResult.NoResult());
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

        // Mirrors the file-endpoint registration emitted by AppModuleDiscoveryGenerator for a
        // Result<ElarionFile> handler: an AOT-safe RequestDelegate binding through ElarionHttpEndpointBinder.
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
            .WithMetadata(new ProducesResponseTypeMetadata(200, null, new[] { "application/octet-stream" }))
            .ProducesElarionErrors()
            .WithMetadata(ElarionFileEndpointMetadata.Instance);

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

        // Mirrors the RequestDelegate registrations emitted by AppModuleDiscoveryGenerator (ADR-0071).
        app.MapGet("/widgets/{id}",
                (RequestDelegate)(static async __context => {
                    var __errors = default(ElarionHttpBindingErrors);
                    var @id = ElarionHttpEndpointBinder.RouteValue<Guid>(__context, "Id", required: true,
                        ref __errors);
                    if (__errors.HasErrors) {
                        await __errors.WriteAsync(__context);
                        return;
                    }
                    var __request = new GetWidgetQuery {
                        Id = @id.GetValueOrDefault()
                    };
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<GetWidgetQuery, Result<WidgetResponse>>>();
                    var __result = ElarionHttpResults.ToResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Widgets.GetWidget")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(WidgetResponse), new[] { "application/json" }))
            .ProducesElarionErrors();

        // The body JsonTypeInfo is resolved once at mapping time; the delegate captures it (non-static).
        var __bodyTypeInfo1 = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<CreateWidgetCommand>(app);
        app.MapPost("/widgets",
                (RequestDelegate)(async __context => {
                    var __bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(__context, __bodyTypeInfo1);
                    if (__bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                        await ElarionHttpEndpointBinder.WriteBodyProblemAsync(__context, __bodyResult.Failure);
                        return;
                    }
                    var __request = __bodyResult.Value!;
                    var __handler = __context.RequestServices
                        .GetRequiredService<IHandler<CreateWidgetCommand, Result<CreateWidgetResponse>>>();
                    var __result = ElarionHttpResults.ToResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithName("Sample.Widgets.CreateWidget")
            .WithMetadata(new ProducesResponseTypeMetadata(200, typeof(CreateWidgetResponse),
                new[] { "application/json" }))
            .ProducesElarionErrors();

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

            // Binding-tier failures (ADR-0071): the binder short-circuits before the handler runs, producing the
            // same RFC 7807 ValidationProblem shape as handler-tier validation.
            var badRoute = await client.GetAsync("/widgets/not-a-guid", ct);
            badRoute.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            badRoute.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            using (var badRouteDoc = JsonDocument.Parse(await badRoute.Content.ReadAsStringAsync(ct))) {
                badRouteDoc.RootElement.GetProperty("errors").TryGetProperty("Id", out _).Should().BeTrue();
            }

            var malformed = await client.PostAsync(
                "/widgets", new StringContent("""{"name":""", Encoding.UTF8, "application/json"), ct);
            malformed.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            malformed.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            using (var malformedDoc = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync(ct))) {
                malformedDoc.RootElement.GetProperty("errors").ValueKind.Should().Be(JsonValueKind.Object);
            }

            var wrongContentType = await client.PostAsync(
                "/widgets", new StringContent("""{"name":"Gadget"}""", Encoding.UTF8, "text/plain"), ct);
            wrongContentType.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
        }
        finally {
            await app.StopAsync(ct);
        }
    }
}
