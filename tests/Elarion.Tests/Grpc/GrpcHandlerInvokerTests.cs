using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Grpc;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Grpc;

public sealed class GrpcHandlerInvokerTests
{
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
        string expectedTrailerValue)
    {
        var exception = GrpcAppErrorTranslator.Default.Translate(new AppError { Kind = kind, Message = "detail" });

        exception.StatusCode.Should().Be(expectedStatus);
        exception.Status.Detail.Should().Be("detail");
        exception.Trailers.GetValue(GrpcAppErrorTranslator.ErrorKindTrailerKey).Should().Be(expectedTrailerValue);
    }

    [Fact]
    public void Translate_UnknownFutureKind_FailsSafeAsInternal()
    {
        var exception = GrpcAppErrorTranslator.Default.Translate(
            new AppError { Kind = (ErrorKind)999, Message = "detail" });

        exception.StatusCode.Should().Be(StatusCode.Internal);
        exception.Trailers.GetValue(GrpcAppErrorTranslator.ErrorKindTrailerKey).Should().Be("internal");
    }

    [Fact]
    public async Task InvokeUnaryAsync_MapsRequestAndResponse_AndSeedsExactBoundaryStateAndCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var callContext = new TestServerCallContext(cancellation.Token);
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("sub", "grpc-user")], authenticationType: "grpc"));
        var captured = new ScopeCapture();
        ServerCallContext? principalFactoryContext = null;
        using var provider = new ServiceCollection()
            .AddScoped<ScopeProbe>()
            .AddSingleton(captured)
            .AddSingleton<IDispatchScopeInitializer, ScopeProbeInitializer>()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, ScopeObservingHandler>()
            .AddElarionGrpcTransport(context =>
            {
                principalFactoryContext = context;
                return principal;
            })
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcHandlerInvoker>();

        var response = await invoker.InvokeUnaryAsync(
            new WireRequest("input"),
            callContext,
            static wire => new ApplicationRequest(wire.Value + "-mapped"),
            static (ApplicationResponse application) => new WireResponse(application.Value + "-mapped"));

        response.Value.Should().Be("input-mapped-mapped");
        principalFactoryContext.Should().BeSameAs(callContext);
        captured.Principal.Should().BeSameAs(principal);
        captured.CallContext.Should().BeSameAs(callContext);
    }

    [Fact]
    public async Task GeneratedServiceOverride_AwaitsInvokerAndMapsSuccessfulResponse()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "grpc"));
        using var provider = new ServiceCollection()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, MappingHandler>()
            .AddElarionGrpcTransport(_ => principal)
            .BuildServiceProvider();
        var service = new GeneratedUnaryService(provider.GetRequiredService<GrpcHandlerInvoker>());

        var response = await service.Unary(
            new WireRequest("input"),
            new TestServerCallContext(CancellationToken.None));

        response.Value.Should().Be("input-request-response");
    }

    [Fact]
    public async Task InvokeUnaryAsync_SelfTypedMarkerInfersBothTypeArguments()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(authenticationType: "grpc"));
        using var provider = new ServiceCollection()
            .AddScoped<IHandler<InferredRequest, Result<ApplicationResponse>>, InferredHandler>()
            .AddElarionGrpcTransport(_ => principal)
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcHandlerInvoker>();

        ApplicationResponse response = await invoker.InvokeUnaryAsync(
            new InferredRequest("input"),
            new TestServerCallContext(CancellationToken.None));

        response.Value.Should().Be("input-inferred");
    }

    [Fact]
    public async Task InvokeUnaryAsync_FailureUsesRegisteredTranslator()
    {
        var translator = new RecordingTranslator();
        using var provider = new ServiceCollection()
            .AddSingleton<IAppErrorTranslator<RpcException>>(translator)
            .AddElarionGrpcTransport(_ => new ClaimsPrincipal())
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>, FailingHandler>()
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcHandlerInvoker>();

        var action = async () => await invoker.InvokeUnaryAsync(
            new WireRequest("ignored"),
            new TestServerCallContext(CancellationToken.None),
            static wire => new ApplicationRequest(wire.Value),
            static (ApplicationResponse application) => new WireResponse(application.Value));

        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.Status.Detail.Should().Be("custom translator");
        translator.Translated.Should().Be(AppError.NotFound("not here"));
    }

    [Fact]
    public async Task InvokeUnaryAsync_ResolvesDecoratedHandlerChain()
    {
        var calls = new CallLog();
        using var provider = new ServiceCollection()
            .AddSingleton(calls)
            .AddScoped<InnerHandler>()
            .AddScoped<IHandler<ApplicationRequest, Result<ApplicationResponse>>>(sp =>
                new RecordingDecorator(sp.GetRequiredService<InnerHandler>(), sp.GetRequiredService<CallLog>()))
            .AddElarionGrpcTransport(_ => new ClaimsPrincipal())
            .BuildServiceProvider();
        var invoker = provider.GetRequiredService<GrpcHandlerInvoker>();

        var response = await invoker.InvokeUnaryAsync(
            new WireRequest("value"),
            new TestServerCallContext(CancellationToken.None),
            static wire => new ApplicationRequest(wire.Value),
            static (ApplicationResponse application) => new WireResponse(application.Value));

        response.Value.Should().Be("value");
        calls.Calls.Should().Equal("decorator", "handler");
    }

    [Fact]
    public void AddElarionGrpcTransport_PreservesCustomTranslator_AndRegistersInvokers()
    {
        var custom = new RecordingTranslator();
        var principal = new ClaimsPrincipal();
        using var provider = new ServiceCollection()
            .AddSingleton<IAppErrorTranslator<RpcException>>(custom)
            .AddElarionGrpcTransport(_ => principal)
            .BuildServiceProvider();

        provider.GetRequiredService<IAppErrorTranslator<RpcException>>().Should().BeSameAs(custom);
        provider.GetRequiredService<IGrpcPrincipalFactory>()
            .CreatePrincipal(new TestServerCallContext(CancellationToken.None)).Should().BeSameAs(principal);
        provider.GetRequiredService<GrpcHandlerInvoker>().Should().NotBeNull();
        provider.GetRequiredService<GrpcStreamHandlerInvoker>().Should().NotBeNull();
    }

    [Fact]
    public void AddElarionGrpcTransport_InstanceOverload_RegistersPrincipalFactory()
    {
        using var provider = new ServiceCollection()
            .AddElarionGrpcTransport(new TestPrincipalFactory())
            .BuildServiceProvider();

        provider.GetRequiredService<IGrpcPrincipalFactory>().Should().BeOfType<TestPrincipalFactory>();
    }

    private sealed record WireRequest(string Value);

    private sealed record WireResponse(string Value);

    private sealed record ApplicationRequest(string Value);

    private sealed record ApplicationResponse(string Value);

    private sealed record InferredRequest(string Value) : IQuery<InferredRequest, ApplicationResponse>;

    private sealed class InferredHandler : IHandler<InferredRequest, Result<ApplicationResponse>>
    {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(InferredRequest request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value + "-inferred"));
    }

    private sealed class ScopeProbe
    {
        public ClaimsPrincipal? Principal { get; set; }

        public ServerCallContext? CallContext { get; set; }
    }

    private sealed class ScopeCapture
    {
        public ClaimsPrincipal? Principal { get; set; }

        public ServerCallContext? CallContext { get; set; }
    }

    private sealed class CallLog
    {
        public List<string> Calls { get; } = [];
    }

    private sealed class ScopeProbeInitializer : IDispatchScopeInitializer
    {
        public void Initialize(IServiceProvider callScope, DispatchScopeContext context)
        {
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

    private sealed class ScopeObservingHandler(ScopeProbe probe) : IHandler<ApplicationRequest, Result<ApplicationResponse>>
    {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct)
        {
            probe.Principal!.FindFirst("sub")!.Value.Should().Be("grpc-user");
            probe.CallContext.Should().BeOfType<TestServerCallContext>();
            ct.Should().Be(probe.CallContext!.CancellationToken);
            return ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
        }
    }

    private abstract class GeneratedUnaryServiceBase
    {
        public abstract Task<WireResponse> Unary(WireRequest request, ServerCallContext context);
    }

    private sealed class GeneratedUnaryService(GrpcHandlerInvoker grpc) : GeneratedUnaryServiceBase
    {
        public override Task<WireResponse> Unary(WireRequest request, ServerCallContext context) =>
            grpc.InvokeUnaryAsync(
                request,
                context,
                static wire => new ApplicationRequest(wire.Value + "-request"),
                static (ApplicationResponse application) => new WireResponse(application.Value + "-response"));
    }

    private sealed class TestPrincipalFactory : IGrpcPrincipalFactory
    {
        public ClaimsPrincipal CreatePrincipal(ServerCallContext context) => new();
    }

    private sealed class MappingHandler : IHandler<ApplicationRequest, Result<ApplicationResponse>>
    {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
    }

    private sealed class FailingHandler : IHandler<ApplicationRequest, Result<ApplicationResponse>>
    {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ApplicationResponse>>(AppError.NotFound("not here"));
    }

    private sealed class InnerHandler(CallLog calls) : IHandler<ApplicationRequest, Result<ApplicationResponse>>
    {
        public ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct)
        {
            calls.Calls.Add("handler");
            return ValueTask.FromResult<Result<ApplicationResponse>>(new ApplicationResponse(request.Value));
        }
    }

    private sealed class RecordingDecorator(
        IHandler<ApplicationRequest, Result<ApplicationResponse>> inner,
        CallLog calls) : IHandler<ApplicationRequest, Result<ApplicationResponse>>
    {
        public async ValueTask<Result<ApplicationResponse>> HandleAsync(ApplicationRequest request, CancellationToken ct)
        {
            calls.Calls.Add("decorator");
            return await inner.HandleAsync(request, ct);
        }
    }

    private sealed class RecordingTranslator : IAppErrorTranslator<RpcException>
    {
        public AppError? Translated { get; private set; }

        public RpcException Translate(AppError error)
        {
            Translated = error;
            return new RpcException(new Status(StatusCode.Aborted, "custom translator"));
        }
    }

    private sealed class TestServerCallContext(CancellationToken cancellationToken) : ServerCallContext
    {
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
