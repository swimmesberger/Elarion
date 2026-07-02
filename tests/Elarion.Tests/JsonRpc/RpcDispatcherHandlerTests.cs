using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Serialization;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// Runtime tests for the JSON-RPC adapter over the handler bus (<see cref="JsonRpcDispatcher"/> resolving an
/// <see cref="IHandler{TRequest,TResponse}"/> from the registry) and <see cref="AppErrorMapper"/>. These exercise
/// the glue the generated <c>RegisterHandlers</c> map depends on.
/// </summary>
public sealed class RpcDispatcherHandlerTests {
    private sealed record EchoCommand {
        public required string Name { get; init; }
    }

    private sealed record EchoResponse(string Greeting);

    private sealed class EchoHandler : IHandler<EchoCommand, Result<EchoResponse>> {
        public ValueTask<Result<EchoResponse>> HandleAsync(EchoCommand request, CancellationToken ct) =>
            request.Name switch {
                "missing" => ValueTask.FromResult<Result<EchoResponse>>(AppError.NotFound("client not found")),
                "dup" => ValueTask.FromResult<Result<EchoResponse>>(AppError.Conflict("already exists")),
                _ => ValueTask.FromResult<Result<EchoResponse>>(new EchoResponse($"Hello {request.Name}")),
            };
    }

    private sealed record CancelCommand {
        public required bool Cooperative { get; init; }
    }

    private sealed class CancelHandler : IHandler<CancelCommand, Result<EchoResponse>> {
        public ValueTask<Result<EchoResponse>> HandleAsync(CancelCommand request, CancellationToken ct) {
            // Cooperative: honor the request token (client disconnect). Non-cooperative: throw an OCE that is not
            // tied to the request token (a handler-internal cancellation), which must still surface as an error.
            if (request.Cooperative) {
                ct.ThrowIfCancellationRequested();
            }

            throw new OperationCanceledException();
        }
    }

    // An all-optional query as a positional record: it has NO public parameterless constructor even though every
    // parameter has a default, so the old Activator.CreateInstance path threw for an omitted `params`. Echoes the
    // resolved values back so a test can assert the constructor defaults were applied.
    private sealed record ListQuery(string? Search = null, int Page = 1, int PageSize = 25);

    private sealed record ListResponse(string? Search, int Page, int PageSize);

    private sealed class ListHandler : IHandler<ListQuery, Result<ListResponse>> {
        public ValueTask<Result<ListResponse>> HandleAsync(ListQuery request, CancellationToken ct) =>
            ValueTask.FromResult<Result<ListResponse>>(
                new ListResponse(request.Search, request.Page, request.PageSize));
    }

    // The framework disables reflection-based serialization by default (AOT discipline); opt these
    // test options in explicitly so the dispatcher can (de)serialize the sample request type.
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static (JsonRpcDispatcher Dispatcher, IServiceProvider Services) Build() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<EchoCommand, EchoResponse>("echo")
            .Map<CancelCommand, EchoResponse>("cancel")
            .Map<ListQuery, ListResponse>("list")
            .Freeze();

        var services = new ServiceCollection()
            .AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>()
            .AddScoped<IHandler<CancelCommand, Result<EchoResponse>>, CancelHandler>()
            .AddScoped<IHandler<ListQuery, Result<ListResponse>>, ListHandler>()
            .BuildServiceProvider();

        return (dispatcher, services);
    }

    private static JsonRpcRequest Request(object @params) =>
        new() {
            Jsonrpc = "2.0",
            Method = "echo",
            Params = JsonSerializer.SerializeToElement(@params, Options),
            Id = "1",
        };

    [Fact]
    public async Task Dispatch_CallerCanceled_RethrowsInsteadOfFabricatingInternalError() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "cancel",
            Params = JsonSerializer.SerializeToElement(new { cooperative = true }, Options),
            Id = "1",
        };

        var act = async () => await dispatcher.DispatchAsync(request, scope.ServiceProvider, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Dispatch_HandlerInternalCancellation_WithLiveToken_SurfacesAsInternalError() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "cancel",
            Params = JsonSerializer.SerializeToElement(new { cooperative = false }, Options),
            Id = "1",
        };

        // The request token is never canceled, so a handler-internal OCE is a genuine fault, not a client abort.
        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-32603);
    }

    [Fact]
    public async Task Dispatch_SuccessfulHandler_ReturnsResponseValue() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(new { name = "World" }), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        response.Result.Should().BeOfType<EchoResponse>()
            .Which.Greeting.Should().Be("Hello World");
    }

    [Fact]
    public async Task Dispatch_OmittedParams_AllOptionalPositionalRecord_AppliesConstructorDefaults() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        // The "params" member is omitted entirely. A positional record has no public parameterless constructor,
        // so the previous Activator.CreateInstance(RequestType) path threw MissingMethodException here and turned
        // a valid all-optional query into a dispatch error. Deserializing "{}" instead applies the ctor defaults.
        var request = new JsonRpcRequest { Jsonrpc = "2.0", Method = "list", Id = "1" };

        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        var result = response.Result.Should().BeOfType<ListResponse>().Which;
        result.Search.Should().BeNull();
        result.Page.Should().Be(1);
        result.PageSize.Should().Be(25);
    }

    [Fact]
    public async Task Dispatch_OmittedParams_ProducesSameObjectAsExplicitEmptyObject() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var omitted = new JsonRpcRequest { Jsonrpc = "2.0", Method = "list", Id = "1" };
        var emptyObject = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "list",
            Params = JsonSerializer.SerializeToElement(new { }, Options),
            Id = "2",
        };

        var fromOmitted = await dispatcher.DispatchAsync(
            omitted, scope.ServiceProvider, TestContext.Current.CancellationToken);
        var fromEmpty = await dispatcher.DispatchAsync(
            emptyObject, scope.ServiceProvider, TestContext.Current.CancellationToken);

        fromOmitted.Error.Should().BeNull();
        fromEmpty.Error.Should().BeNull();
        // Omitted params and an explicit {} are indistinguishable: both apply the request record's defaults.
        fromOmitted.Result.Should().BeEquivalentTo(fromEmpty.Result);
    }

    [Fact]
    public async Task Dispatch_PartialParams_AppliesDefaultsForOmittedMembers() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "list",
            Params = JsonSerializer.SerializeToElement(new { page = 3 }, Options),
            Id = "1",
        };

        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        var result = response.Result.Should().BeOfType<ListResponse>().Which;
        result.Page.Should().Be(3);
        result.PageSize.Should().Be(25);
    }

    [Fact]
    public async Task Dispatch_OmittedParams_RequiredMemberType_ReturnsInvalidParams() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        // EchoCommand.Name is `required`. Deserializing an omitted params as {} correctly fails the missing
        // required member as invalid params (-32602) rather than — as the old Activator.CreateInstance path did —
        // silently constructing an EchoCommand with a null Name and running the handler on invalid input.
        var request = new JsonRpcRequest { Jsonrpc = "2.0", Method = "echo", Id = "1" };

        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Result.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-32602);
    }

    [Theory]
    [InlineData("missing", -32001)] // ErrorKind.NotFound
    [InlineData("dup", -32002)]     // ErrorKind.Conflict
    public async Task Dispatch_HandlerReturnsAppError_MapsToJsonRpcErrorCode(string name, int expectedCode) {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(new { name }), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Result.Should().BeNull();
        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(expectedCode);
    }

    private sealed class FixedCodeErrorTranslator : IAppErrorTranslator<RpcError> {
        public RpcError Translate(AppError error) => new() { Code = -40404, Message = $"custom: {error.Message}" };
    }

    [Fact]
    public async Task Dispatch_RegisteredErrorTranslator_OverridesErrorCode() {
        var dispatcher = new JsonRpcDispatcher(Options).Map<EchoCommand, EchoResponse>("echo").Freeze();
        var services = new ServiceCollection()
            .AddScoped<IHandler<EchoCommand, Result<EchoResponse>>, EchoHandler>()
            .AddSingleton<IAppErrorTranslator<RpcError>, FixedCodeErrorTranslator>()
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Request(new { name = "missing" }), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-40404);
        response.Error.Message.Should().StartWith("custom:");
    }

    [Fact]
    public void AppErrorMapper_PreservesMessageAndData() {
        var data = new { field = "name" };
        var error = AppErrorMapper.ToRpcError(AppError.Validation("name is required", data));

        error.Code.Should().Be(-32602);
        error.Message.Should().Be("name is required");
        error.Data.Should().BeSameAs(data);
    }

    [Fact]
    public void ErrorResponse_WithValidationData_SerializesUnderCanonicalOptions_WithoutReflection() {
        // Mirror the JSON-RPC host: the canonical options with the envelope context inserted first and reflection
        // OFF (the AOT-honest default). The framework's ValidationErrorData payload has no app-registered context
        // here, so before it was seeded into the canonical chain this threw NotSupportedException => HTTP 500
        // instead of the -32602 it should return.
        var services = new ServiceCollection();
        services.ConfigureElarionJson(o => o.TypeInfoResolvers.Insert(0, JsonRpcJsonContext.Default));
        var options = services.BuildServiceProvider().GetRequiredService<IElarionJsonSerialization>().Options;

        var response = JsonRpcResponse.FromError(
            "1", AppErrorMapper.ToRpcError(AppError.Validation("invalid", (IReadOnlyList<string>)["name is required"])));

        var json = JsonSerializer.Serialize(response, options.GetTypeInfo(typeof(JsonRpcResponse)));

        json.Should().Contain("\"code\":-32602");
        json.Should().Contain("\"errors\":[\"name is required\"]");
    }
}
