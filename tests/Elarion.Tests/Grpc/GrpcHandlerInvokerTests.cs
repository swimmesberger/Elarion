using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Grpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Grpc;

public sealed class GrpcHandlerInvokerTests {
    [Theory]
    [InlineData(ErrorKind.Validation, StatusCode.InvalidArgument, "validation")]
    [InlineData(ErrorKind.NotFound, StatusCode.NotFound, "not-found")]
    [InlineData(ErrorKind.Conflict, StatusCode.AlreadyExists, "conflict")]
    [InlineData(ErrorKind.Forbidden, StatusCode.PermissionDenied, "forbidden")]
    [InlineData(ErrorKind.Unauthorized, StatusCode.Unauthenticated, "unauthorized")]
    [InlineData(ErrorKind.BusinessRule, StatusCode.FailedPrecondition, "business-rule")]
    [InlineData(ErrorKind.Internal, StatusCode.Internal, "internal")]
    public void Translate_MapsKnownKindsToStableStatusAndTrailer(
        ErrorKind kind,
        StatusCode expectedStatus,
        string expectedTrailerValue) {
        var exception = GrpcAppErrorTranslator.Default.Translate(new AppError { Kind = kind, Message = "detail" });

        exception.StatusCode.Should().Be(expectedStatus);
        exception.Status.Detail.Should().Be("detail");
        exception.Trailers.GetValue(GrpcAppErrorTranslator.ErrorKindTrailerKey).Should().Be(expectedTrailerValue);
    }

    [Fact]
    public void Translate_UnknownFutureKind_FailsSafeAsInternal() {
        var exception = GrpcAppErrorTranslator.Default.Translate(
            new AppError { Kind = (ErrorKind)999, Message = "detail" });

        exception.StatusCode.Should().Be(StatusCode.Internal);
        exception.Trailers.GetValue(GrpcAppErrorTranslator.ErrorKindTrailerKey).Should().Be("internal");
    }

    [Fact]
    public async Task InvokeUnaryAsync_MapsRequestAndResponse_AndSeedsExactBoundaryStateAndCancellation() {
        using var cancellation = new CancellationTokenSource();
        var callContext = new TestServerCallContext(cancellation.Token);
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "grpc-user")], authenticationType: "grpc"));
        var captured = new ScopeCapture();
        using var provider = new ServiceCollection()
            .AddScoped<ScopeProbe>()
            .AddSingleton(captured)
            .AddSingleton<IDispatchScopeInitializer, ScopeProbeInitializer>()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, ScopeObservingHandler>()
            .BuildServiceProvider();

        var response = await GrpcHandlerInvoker.InvokeUnaryAsync<WireRequest, WireResponse, ApplicationRequest, ApplicationResponse>(
            provider,
            new WireRequest("input"),
            callContext,
            principal,
            static wire => new ApplicationRequest(wire.Value + "-mapped"),
            static application => new WireResponse(application.Value + "-mapped"));

        response.Value.Should().Be("input-mapped-mapped");
        captured.Principal.Should().BeSameAs(principal);
        captured.CallContext.Should().BeSameAs(callContext);
    }

    [Fact]
    public async Task GeneratedServiceOverride_AwaitsInvokerAndMapsSuccessfulResponse() {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "grpc"));
        using var provider = new ServiceCollection()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, MappingHandler>()
            .BuildServiceProvider();
        var service = new GeneratedUnaryService(provider, principal);

        var response = await service.Unary(
            new WireRequest("input"),
            new TestServerCallContext(CancellationToken.None));

        response.Value.Should().Be("input-request-response");
    }

    [Fact]
    public async Task InvokeUnaryAsync_FailureUsesRegisteredTranslator() {
        var translator = new RecordingTranslator();
        using var provider = new ServiceCollection()
            .AddSingleton<IAppErrorTranslator<RpcException>>(translator)
            .AddElarionGrpcTransport()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, FailingHandler>()
            .BuildServiceProvider();

        var action = async () => await GrpcHandlerInvoker.InvokeUnaryAsync<WireRequest, WireResponse, ApplicationRequest, ApplicationResponse>(
            provider,
            new WireRequest("ignored"),
            new TestServerCallContext(CancellationToken.None),
            new ClaimsPrincipal(),
            static wire => new ApplicationRequest(wire.Value),
            static application => new WireResponse(application.Value));

        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.Status.Detail.Should().Be("custom translator");
        translator.Translated.Should().Be(AppError.NotFound("not here"));
    }

    [Fact]
    public async Task InvokeUnaryAsync_ResolvesDecoratedHandlerChain() {
        var calls = new CallLog();
        using var provider = new ServiceCollection()
            .AddSingleton(calls)
            .AddScoped<InnerHandler>()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>>(sp =>
                new RecordingDecorator(sp.GetRequiredService<InnerHandler>(), sp.GetRequiredService<CallLog>()))
            .BuildServiceProvider();

        var response = await GrpcHandlerInvoker.InvokeUnaryAsync<WireRequest, WireResponse, ApplicationRequest, ApplicationResponse>(
            provider,
            new WireRequest("value"),
            new TestServerCallContext(CancellationToken.None),
            new ClaimsPrincipal(),
            static wire => new ApplicationRequest(wire.Value),
            static application => new WireResponse(application.Value));

        response.Value.Should().Be("value");
        calls.Calls.Should().Equal("decorator", "handler");
    }

    [Fact]
    public void AddElarionGrpcTransport_PreservesCustomTranslator() {
        var custom = new RecordingTranslator();
        using var provider = new ServiceCollection()
            .AddSingleton<IAppErrorTranslator<RpcException>>(custom)
            .AddElarionGrpcTransport()
            .BuildServiceProvider();

        provider.GetRequiredService<IAppErrorTranslator<RpcException>>().Should().BeSameAs(custom);
    }

    private sealed record WireRequest(string Value);

    private sealed record WireResponse(string Value);

    private sealed record ApplicationRequest(string Value);

    private sealed record ApplicationResponse(string Value);

    private sealed class ScopeProbe {
        public ClaimsPrincipal? Principal { get; set; }

        public ServerCallContext? CallContext { get; set; }
    }

    private sealed class ScopeCapture {
        public ClaimsPrincipal? Principal { get; set; }

        public ServerCallContext? CallContext { get; set; }
    }

    private sealed class CallLog {
        public List<string> Calls { get; } = [];
    }

    private sealed class ScopeProbeInitializer : IDispatchScopeInitializer {
        public void Initialize(IServiceProvider callScope, DispatchScopeContext context) {
            var probe = callScope.GetRequiredService<ScopeProbe>();
            context.TryGet(out ClaimsPrincipal? principal).Should().BeTrue();
            context.TryGet(out ServerCallContext? callContext).Should().BeTrue();
            probe.Principal = principal;
            probe.CallContext = callContext;
            var capture = callScope.GetRequiredService<ScopeCapture>();
            capture.Principal = principal;
            capture.CallContext = callContext;
        }
    }

    private sealed class ScopeObservingHandler(ScopeProbe probe) : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) {
            probe.Principal!.FindFirst("sub")!.Value.Should().Be("grpc-user");
            probe.CallContext.Should().BeOfType<TestServerCallContext>();
            ct.Should().Be(probe.CallContext!.CancellationToken);
            return ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
        }
    }

    private abstract class GeneratedUnaryServiceBase {
        public abstract Task<WireResponse> Unary(WireRequest request, ServerCallContext context);
    }

    private sealed class GeneratedUnaryService(
        IServiceProvider services,
        ClaimsPrincipal principal) : GeneratedUnaryServiceBase {
        public override async Task<WireResponse> Unary(WireRequest request, ServerCallContext context) {
            return await GrpcHandlerInvoker.InvokeUnaryAsync<
                WireRequest, WireResponse, ApplicationRequest, ApplicationResponse>(
                services,
                request,
                context,
                principal,
                static wire => new ApplicationRequest(wire.Value + "-request"),
                static application => new WireResponse(application.Value + "-response"));
        }
    }

    private sealed class MappingHandler : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
    }

    private sealed class FailingHandler : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ApplicationResponse>>(AppError.NotFound("not here"));
    }

    private sealed class InnerHandler(CallLog calls) : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) {
            calls.Calls.Add("handler");
            return ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
        }
    }

    private sealed class RecordingDecorator(
        IHandler<ApplicationRequest, Result<ApplicationResponse>> inner,
        CallLog calls) : IHandler<ApplicationRequest, Result<ApplicationResponse>> {
        public async ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) {
            calls.Calls.Add("decorator");
            return await inner.HandleAsync(request, ct);
        }
    }

    private sealed class RecordingTranslator : IAppErrorTranslator<RpcException> {
        public AppError? Translated { get; private set; }

        public RpcException Translate(AppError error) {
            Translated = error;
            return new RpcException(new Status(StatusCode.Aborted, "custom translator"));
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

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
