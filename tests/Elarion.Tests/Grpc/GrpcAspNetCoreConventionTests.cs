using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Grpc;
using Elarion.Grpc.AspNetCore;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Grpc;

public sealed class GrpcAspNetCoreConventionTests {
    [Fact]
    public async Task AddElarion_AndContextExtension_UseAspNetCoreCallConventions() {
        using var cancellation = new CancellationTokenSource();
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "aspnet-grpc-user")], "grpc"));
        var capture = new CallCapture();
        var services = new ServiceCollection()
            .AddSingleton(capture)
            .AddSingleton<IDispatchScopeInitializer, CaptureInitializer>()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, MappingHandler>();
        var grpc = services.AddGrpc();

        grpc.AddElarion().Should().BeSameAs(grpc);

        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext {
            RequestServices = provider,
            User = principal
        };
        var callContext = new TestServerCallContext(cancellation.Token);
        callContext.UserState["__HttpContext"] = httpContext;
        var service = new GeneratedUnaryService();

        var response = await service.Unary(new WireRequest("input"), callContext);

        response.Value.Should().Be("input-request-response");
        capture.Principal.Should().BeSameAs(principal);
        capture.CallContext.Should().BeSameAs(callContext);
        capture.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task InvokeElarionAsync_TranslatesFailedResult() {
        var services = new ServiceCollection()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, FailingHandler>();
        services.AddGrpc().AddElarion();
        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var callContext = new TestServerCallContext(CancellationToken.None);
        callContext.UserState["__HttpContext"] = httpContext;

        var action = async () => await callContext.InvokeElarionAsync<ApplicationRequest, ApplicationResponse>(
            new ApplicationRequest("ignored"));

        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
        exception.Which.Trailers.GetValue(GrpcAppErrorTranslator.ErrorKindTrailerKey).Should().Be("not-found");
    }

    [Fact]
    public async Task InvokeElarionStreamAsync_UsesAspNetCorePrincipalAndRequestServices() {
        using var cancellation = new CancellationTokenSource();
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "aspnet-grpc-stream-user")], "grpc"));
        var capture = new CallCapture();
        var services = new ServiceCollection()
            .AddSingleton(capture)
            .AddSingleton<IDispatchScopeInitializer, CaptureInitializer>()
            .AddScoped<IStreamHandler<ApplicationRequest, ApplicationResponse>, StreamingHandler>();
        services.AddGrpc().AddElarion();
        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext {
            RequestServices = provider,
            User = principal
        };
        var callContext = new TestServerCallContext(cancellation.Token);
        callContext.UserState["__HttpContext"] = httpContext;

        await using var stream = await callContext.InvokeElarionStreamAsync<ApplicationRequest, ApplicationResponse>(
            new ApplicationRequest("input"));
        var values = new List<string>();
        await foreach (var item in stream.WithCancellation(cancellation.Token))
            values.Add(item.Value);

        values.Should().Equal("input-1", "input-2");
        capture.Principal.Should().BeSameAs(principal);
        capture.CallContext.Should().BeSameAs(callContext);
        capture.CancellationToken.Should().Be(cancellation.Token);
    }

    private sealed record WireRequest(string Value);

    private sealed record WireResponse(string Value);

    private sealed record ApplicationRequest(string Value);

    private sealed record ApplicationResponse(string Value);

    private sealed class CallCapture {
        public ClaimsPrincipal? Principal { get; set; }

        public ServerCallContext? CallContext { get; set; }

        public CancellationToken CancellationToken { get; set; }
    }

    private sealed class CaptureInitializer : IDispatchScopeInitializer {
        public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
            var capture = callScope.GetRequiredService<CallCapture>();
            context.TryGet(out ClaimsPrincipal? principal).Should().BeTrue();
            context.TryGet(out ServerCallContext? callContext).Should().BeTrue();
            capture.Principal = principal;
            capture.CallContext = callContext;
        }
    }

    private abstract class GeneratedUnaryServiceBase {
        public abstract Task<WireResponse> Unary(WireRequest request, ServerCallContext context);
    }

    private sealed class GeneratedUnaryService : GeneratedUnaryServiceBase {
        public override async Task<WireResponse> Unary(WireRequest request, ServerCallContext context) {
            var response = await context.InvokeElarionAsync<ApplicationRequest, ApplicationResponse>(
                new ApplicationRequest(request.Value + "-request"));
            return new WireResponse(response.Value + "-response");
        }
    }

    private sealed class MappingHandler(CallCapture capture)
        : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) {
            capture.CancellationToken = ct;
            return ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
        }
    }

    private sealed class FailingHandler : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) {
            return ValueTask.FromResult<Result<ApplicationResponse>>(AppError.NotFound("not here"));
        }
    }

    private sealed class StreamingHandler(CallCapture capture)
        : IStreamHandler<ApplicationRequest, ApplicationResponse> {
        public ValueTask<Result<IAsyncEnumerable<ApplicationResponse>>> HandleAsync(
            ApplicationRequest request,
            CancellationToken ct) {
            capture.CancellationToken = ct;
            return ValueTask.FromResult(Result<IAsyncEnumerable<ApplicationResponse>>.Success(Values(request)));
        }

        private static async IAsyncEnumerable<ApplicationResponse> Values(ApplicationRequest request) {
            yield return new ApplicationResponse(request.Value + "-1");
            await Task.Yield();
            yield return new ApplicationResponse(request.Value + "-2");
        }
    }

    private sealed class TestServerCallContext(CancellationToken cancellationToken) : ServerCallContext {
        protected override string MethodCore => "/test.Service/Method";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "ipv4:127.0.0.1:1";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore { get; } = [];

        protected override CancellationToken CancellationTokenCore => cancellationToken;

        protected override Metadata ResponseTrailersCore { get; } = [];

        protected override Status StatusCore { get; set; }

        protected override WriteOptions? WriteOptionsCore { get; set; }

        protected override AuthContext AuthContextCore { get; } = new("insecure", []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) {
            throw new NotSupportedException();
        }

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) {
            return Task.CompletedTask;
        }
    }
}
