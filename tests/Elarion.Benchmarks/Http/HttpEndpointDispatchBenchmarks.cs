using System.CodeDom.Compiler;
using System.Text;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Elarion.Benchmarks.Http;

/// <summary>
/// The per-request cost of one generated <c>[HttpEndpoint]</c> dispatch (ADR-0071) against the strongest
/// available baseline: the SAME endpoint shape compiled by ASP.NET Core's Request Delegate Generator. RDG can
/// only ever intercept hand-written call sites, so the baseline arms here are hand-written typed lambdas in
/// this compilation (RDG-intercepted — the setup throws if interception did not happen), while the Elarion arms
/// hand-copy the exact code the module bootstrapper generator emits. Both invoke the extracted
/// <see cref="RequestDelegate"/>s directly on a fresh <see cref="DefaultHttpContext"/> — no server, no routing
/// middleware — so the numbers isolate binding + handler dispatch + result writing.
/// <para>
/// The gate (ADR-0071): the generated shape must be at least as fast and allocation-lean as the RDG output for
/// the same endpoint. <c>ContextOnly</c> measures the shared per-op harness cost (context + response plumbing)
/// to subtract mentally from both arms.
/// </para>
/// <code>
///   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*HttpEndpointDispatch*"
/// </code>
/// </summary>
[MemoryDiagnoser]
public class HttpEndpointDispatchBenchmarks {
    private WebApplication _app = null!;
    private IServiceProvider _services = null!;
    private RequestDelegate _rdgGet = null!;
    private RequestDelegate _generatedGet = null!;
    private RequestDelegate _rdgPost = null!;
    private RequestDelegate _generatedPost = null!;
    private byte[] _orderJson = null!;

    [GlobalSetup]
    public void Setup() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHandler<GetQuoteQuery, Result<QuoteResponse>>, GetQuoteHandler>();
        builder.Services.AddSingleton<IHandler<CreateOrderCommand, Result<OrderResponse>>, CreateOrderHandler>();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HttpDispatchJsonContext.Default));

        _app = builder.Build();
        var app = _app;

        // Baseline arms: hand-written typed lambdas — the shape the pre-ADR-0071 generator emitted — which RDG
        // intercepts because they are hand-written in THIS compilation.
        app.MapGet("/rdg/quotes/{symbol}", static async (
                [AsParameters] GetQuoteQuery request,
                [FromServices] IHandler<GetQuoteQuery, Result<QuoteResponse>> handler,
                CancellationToken ct) =>
            ElarionHttpResults.ToResult(await handler.HandleAsync(request, ct)));
        app.MapPost("/rdg/orders", static async (
                CreateOrderCommand request,
                [FromServices] IHandler<CreateOrderCommand, Result<OrderResponse>> handler,
                CancellationToken ct) =>
            ElarionHttpResults.ToResult(await handler.HandleAsync(request, ct)));

        // Elarion arms: byte-for-byte the registration the module bootstrapper generator emits (keep in sync
        // with HttpEndpointEmission.Emit.cs / a fresh EmitCompilerGeneratedFiles build of samples/LiveQuotes).
        app.MapGet("/gen/quotes/{symbol}", (RequestDelegate)(static async __context => {
            var __errors = default(ElarionHttpBindingErrors);
            var @symbol = ElarionHttpEndpointBinder.RouteString(__context, "Symbol", required: true, ref __errors);
            if (__errors.HasErrors) {
                await __errors.WriteAsync(__context);
                return;
            }
            var __request = new GetQuoteQuery(@symbol!);
            var __handler = __context.RequestServices
                .GetRequiredService<IHandler<GetQuoteQuery, Result<QuoteResponse>>>();
            var __result = ElarionHttpResults.ToResult(
                await __handler.HandleAsync(__request, __context.RequestAborted));
            await __result.ExecuteAsync(__context);
        }));
        var __bodyTypeInfo0 = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<CreateOrderCommand>(app);
        app.MapPost("/gen/orders", (RequestDelegate)(async __context => {
            var __bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(__context, __bodyTypeInfo0);
            if (__bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                await ElarionHttpEndpointBinder.WriteBodyProblemAsync(__context, __bodyResult.Failure);
                return;
            }
            var __request = __bodyResult.Value!;
            var __handler = __context.RequestServices
                .GetRequiredService<IHandler<CreateOrderCommand, Result<OrderResponse>>>();
            var __result = ElarionHttpResults.ToResult(
                await __handler.HandleAsync(__request, __context.RequestAborted));
            await __result.ExecuteAsync(__context);
        }));

        _services = _app.Services;
        _rdgGet = ExtractDelegate("/rdg/quotes/{symbol}", requireRdg: true);
        _generatedGet = ExtractDelegate("/gen/quotes/{symbol}", requireRdg: false);
        _rdgPost = ExtractDelegate("/rdg/orders", requireRdg: true);
        _generatedPost = ExtractDelegate("/gen/orders", requireRdg: false);

        _orderJson = Encoding.UTF8.GetBytes("""{"symbol":"ELN","quantity":10,"limitPrice":141.52}""");

        // Honesty guard: every arm must actually execute its happy path — a mis-set-up arm that fails fast
        // would "win" the benchmark while measuring its error leg.
        AssertOk(nameof(RdgRouteBind), _rdgGet, CreateGetContext());
        AssertOk(nameof(GeneratedRouteBind), _generatedGet, CreateGetContext());
        AssertOk(nameof(RdgJsonBody), _rdgPost, CreatePostContext());
        AssertOk(nameof(GeneratedJsonBody), _generatedPost, CreatePostContext());
    }

    private static void AssertOk(string arm, RequestDelegate endpoint, DefaultHttpContext context) {
        endpoint(context).GetAwaiter().GetResult();
        if (context.Response.StatusCode != StatusCodes.Status200OK)
            throw new InvalidOperationException(
                $"Benchmark arm '{arm}' responded {context.Response.StatusCode}, expected 200 — it is not "
                + "measuring the happy path.");
    }

    [GlobalCleanup]
    public Task Cleanup() {
        return _app.DisposeAsync().AsTask();
    }

    /// <summary>The shared per-op harness cost (context construction + fields) both arms pay identically.</summary>
    [Benchmark]
    public HttpContext ContextOnly() {
        return CreateGetContext();
    }

    [Benchmark(Baseline = true)]
    public Task RdgRouteBind() {
        return _rdgGet(CreateGetContext());
    }

    [Benchmark]
    public Task GeneratedRouteBind() {
        return _generatedGet(CreateGetContext());
    }

    [Benchmark]
    public Task RdgJsonBody() {
        return _rdgPost(CreatePostContext());
    }

    [Benchmark]
    public Task GeneratedJsonBody() {
        return _generatedPost(CreatePostContext());
    }

    private DefaultHttpContext CreateGetContext() {
        var context = new DefaultHttpContext { RequestServices = _services };
        context.Request.RouteValues["symbol"] = "ELN";
        context.Response.Body = Stream.Null;
        return context;
    }

    private DefaultHttpContext CreatePostContext() {
        var context = new DefaultHttpContext { RequestServices = _services };
        // A fresh read-only stream over the shared payload per op: identical cost in both arms, and no
        // cross-op stream state (a consumed/completed body would silently flip an arm onto its error leg).
        context.Request.Body = new MemoryStream(_orderJson, writable: false);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = _orderJson.Length;
        // Kestrel always provides body detection; without it RDG's generated binding concludes the request
        // cannot have a body and 400s (the setup honesty guard catches exactly this).
        context.Features.Set<Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature>(
            BodyDetection.Instance);
        context.Response.Body = Stream.Null;
        return context;
    }

    private sealed class BodyDetection : Microsoft.AspNetCore.Http.Features.IHttpRequestBodyDetectionFeature {
        public static readonly BodyDetection Instance = new();

        public bool CanHaveBody => true;
    }

    private RequestDelegate ExtractDelegate(string pattern, bool requireRdg) {
        var endpoint = ((IEndpointRouteBuilder)_app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern);

        // Honesty guard: the baseline must be the RDG-compiled delegate, not the runtime
        // RequestDelegateFactory fallback — RDG stamps a GeneratedCodeAttribute into the endpoint metadata.
        var intercepted = endpoint.Metadata.OfType<GeneratedCodeAttribute>()
            .Any(a => a.Tool?.Contains("RequestDelegateGenerator", StringComparison.Ordinal) == true);
        if (requireRdg && !intercepted)
            throw new InvalidOperationException(
                $"RDG did not intercept '{pattern}'; the baseline would silently measure the reflection-based "
                + "RequestDelegateFactory. Check EnableRequestDelegateGenerator/InterceptorsNamespaces.");

        return endpoint.RequestDelegate
               ?? throw new InvalidOperationException($"Endpoint '{pattern}' has no request delegate.");
    }

    public sealed record GetQuoteQuery(string Symbol);

    public sealed record QuoteResponse(string Symbol, decimal Price, long Seq);

    public sealed record CreateOrderCommand {
        public required string Symbol { get; init; }
        public required int Quantity { get; init; }
        public required decimal LimitPrice { get; init; }
    }

    public sealed record OrderResponse(Guid Id, string Symbol, int Quantity);

    private sealed class GetQuoteHandler : IHandler<GetQuoteQuery, Result<QuoteResponse>> {
        private static readonly Result<QuoteResponse> Response = new QuoteResponse("ELN", 141.52m, 42);

        public ValueTask<Result<QuoteResponse>> HandleAsync(GetQuoteQuery request, CancellationToken ct) {
            return ValueTask.FromResult(Response);
        }
    }

    private sealed class CreateOrderHandler : IHandler<CreateOrderCommand, Result<OrderResponse>> {
        private static readonly Result<OrderResponse> Response =
            new OrderResponse(Guid.Parse("019888a2-0000-7000-8000-000000000000"), "ELN", 10);

        public ValueTask<Result<OrderResponse>> HandleAsync(CreateOrderCommand request, CancellationToken ct) {
            return ValueTask.FromResult(Response);
        }
    }
}

[JsonSerializable(typeof(HttpEndpointDispatchBenchmarks.GetQuoteQuery))]
[JsonSerializable(typeof(HttpEndpointDispatchBenchmarks.QuoteResponse))]
[JsonSerializable(typeof(HttpEndpointDispatchBenchmarks.CreateOrderCommand))]
[JsonSerializable(typeof(HttpEndpointDispatchBenchmarks.OrderResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class HttpDispatchJsonContext : JsonSerializerContext;
