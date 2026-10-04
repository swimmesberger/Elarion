using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.AspNetCore;
using Elarion.JsonRpc;
using Elarion.JsonRpc.Mcp;
using Elarion.Pipeline;
using Elarion.Tests.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Elarion.Authorization;

namespace Elarion.Tests.Authorization;

/// <summary>
/// A caller who may not call an operation must get the authentication/authorization error rather than a payload
/// error (401/403 before 400), on every transport that binds a wire payload itself: JSON-RPC, MCP tool calls and the
/// generated HTTP endpoints. The pipeline still runs unchanged when the payload binds (so denials stay audited and
/// traced); the handler gate only decides which error a request that never reaches it reports.
/// </summary>
public sealed class PreBindingAdmissionTests {
    private const string MalformedParams = """{"name":42}""";
    private const string ValidParams = """{"name":"ok"}""";

    private sealed record AdminCommand {
        public required string Name { get; init; }
    }

    private sealed record OpenCommand {
        public required string Name { get; init; }
    }

    private sealed record Reply(string Greeting);

    [RequireRole("admin")]
    private sealed class AdminHandler : IHandler<AdminCommand, Result<Reply>> {
        public ValueTask<Result<Reply>> HandleAsync(AdminCommand request, CancellationToken ct) {
            return ValueTask.FromResult<Result<Reply>>(new Reply("hi " + request.Name));
        }
    }

    [AllowAnonymous]
    private sealed class OpenHandler : IHandler<OpenCommand, Result<Reply>> {
        public ValueTask<Result<Reply>> HandleAsync(OpenCommand request, CancellationToken ct) {
            return ValueTask.FromResult<Result<Reply>>(new Reply("hi " + request.Name));
        }
    }

    private sealed class UserHolder {
        public ICurrentUser User { get; set; } = new FakeCurrentUser();
    }

    private static FakeCurrentUser Anonymous => new() { IsAuthenticated = false };

    private static FakeCurrentUser Member => new() { IsAuthenticated = true };

    private static FakeCurrentUser Admin => new() { IsAuthenticated = true, Roles = ["admin"] };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static void Register(IServiceCollection services, UserHolder holder) {
        services.AddLogging();
        services.AddSingleton(holder);
        services.AddScoped<ICurrentUser>(_ => holder.User);
        services.AddElarionAuthorization();

        var adminMetadata = new HandlerMetadata(typeof(AdminHandler), typeof(AdminCommand), typeof(Result<Reply>));
        services.AddScoped<IHandler<AdminCommand, Result<Reply>>>(sp =>
            new AuthorizationDecorator<AdminCommand, Result<Reply>>(
                new AdminHandler(), adminMetadata, sp.GetRequiredService<IAuthorizer>()));
        // What the generated registration emits for a handler with an authorization decorator.
        services.AddKeyedSingleton<IHandlerGate>(typeof(AdminCommand), new AuthorizationGate(adminMetadata));

        // An [AllowAnonymous] handler has no authorization decorator, hence no gate.
        services.AddScoped<IHandler<OpenCommand, Result<Reply>>, OpenHandler>();
    }

    private static (JsonRpcDispatcher Dispatcher, ServiceProvider Services, UserHolder Holder) BuildRpc() {
        var holder = new UserHolder();
        var services = new ServiceCollection();
        Register(services, holder);
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<AdminCommand, Reply>("admin.greet")
            .Map<OpenCommand, Reply>("open.greet")
            .Freeze();
        return (dispatcher, services.BuildServiceProvider(), holder);
    }

    private static JsonRpcRequest Call(string method, string paramsJson) {
        return new JsonRpcRequest {
            Jsonrpc = "2.0", Method = method, Id = "1", Params = JsonDocument.Parse(paramsJson).RootElement
        };
    }

    [Theory]
    [InlineData(MalformedParams, -32005)]
    [InlineData(ValidParams, -32005)]
    public async Task JsonRpc_UnauthenticatedCaller_GetsUnauthorized_WhateverTheParams(string json, int code) {
        var (dispatcher, services, holder) = BuildRpc();
        holder.User = Anonymous;
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Call("admin.greet", json), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error!.Code.Should().Be(code);
    }

    [Fact]
    public async Task JsonRpc_AuthenticatedCallerWithoutTheRole_GetsForbidden_ForMalformedParams() {
        var (dispatcher, services, holder) = BuildRpc();
        holder.User = Member;
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Call("admin.greet", MalformedParams), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error!.Code.Should().Be(-32003);
    }

    [Fact]
    public async Task JsonRpc_AdmittedCaller_GetsInvalidParams_ForMalformedParams_AndSuccessForValid() {
        var (dispatcher, services, holder) = BuildRpc();
        holder.User = Admin;
        await using var scope = services.CreateAsyncScope();

        var malformed = await dispatcher.DispatchAsync(
            Call("admin.greet", MalformedParams), scope.ServiceProvider, TestContext.Current.CancellationToken);
        var valid = await dispatcher.DispatchAsync(
            Call("admin.greet", ValidParams), scope.ServiceProvider, TestContext.Current.CancellationToken);

        malformed.Error!.Code.Should().Be(-32602);
        valid.Error.Should().BeNull();
    }

    [Fact]
    public async Task JsonRpc_OperationWithoutAGate_StillReportsInvalidParams_ToAnonymousCallers() {
        var (dispatcher, services, holder) = BuildRpc();
        holder.User = Anonymous;
        await using var scope = services.CreateAsyncScope();

        var response = await dispatcher.DispatchAsync(
            Call("open.greet", MalformedParams), scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error!.Code.Should().Be(-32602);
    }

    [Fact]
    public async Task Mcp_ToolCall_FollowsTheSamePrecedence() {
        var (dispatcher, services, holder) = BuildRpc();
        var arguments = JsonDocument.Parse(MalformedParams).RootElement;

        holder.User = Anonymous;
        var anonymous = await RpcToolInvoker.InvokeAsync(dispatcher.Registry, HandlerTransports.Mcp, "admin.greet",
            arguments, services, Options, ct: TestContext.Current.CancellationToken);
        holder.User = Member;
        var member = await RpcToolInvoker.InvokeAsync(dispatcher.Registry, HandlerTransports.Mcp, "admin.greet",
            arguments, services, Options, ct: TestContext.Current.CancellationToken);
        holder.User = Admin;
        var admin = await RpcToolInvoker.InvokeAsync(dispatcher.Registry, HandlerTransports.Mcp, "admin.greet",
            arguments, services, Options, ct: TestContext.Current.CancellationToken);

        anonymous.ErrorCode.Should().Be(-32005);
        member.ErrorCode.Should().Be(-32003);
        admin.ErrorCode.Should().Be(-32602);
    }

    [Fact]
    public async Task Http_GeneratedEndpointShape_FollowsTheSamePrecedence() {
        var ct = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddHttpContextAccessor();
        var holder = new UserHolder();
        Register(builder.Services, holder);
        // The request user follows a header so one host serves every case.
        builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<IHttpContextAccessor>().HttpContext!
            .Request.Headers["x-user"].ToString() switch {
                "admin" => Admin,
                "member" => Member,
                _ => Anonymous
            });
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        await using var app = builder.Build();

        // Mirrors the body endpoint registration emitted by AppModuleDiscoveryGenerator, including the admission
        // check on a binding failure.
        var bodyTypeInfo = ElarionHttpEndpointBinder.ResolveBodyTypeInfo<AdminCommand>(app);
        app.MapPost("/admin/greet", (RequestDelegate)(async __context => {
            var __bodyResult = await ElarionHttpEndpointBinder.ReadJsonBodyAsync(__context, bodyTypeInfo);
            if (__bodyResult.Failure != ElarionHttpEndpointBinder.BodyFailure.None) {
                if (!await ElarionHttpEndpointBinder.TryAdmitAsync<AdminCommand>(__context)) return;
                await ElarionHttpEndpointBinder.WriteBodyProblemAsync(__context, __bodyResult.Failure);
                return;
            }

            var __handler = __context.RequestServices.GetRequiredService<IHandler<AdminCommand, Result<Reply>>>();
            var __result = ElarionHttpResults.ToResult(
                await __handler.HandleAsync(__bodyResult.Value!, __context.RequestAborted));
            await __result.ExecuteAsync(__context);
        }));
        await app.StartAsync(ct);

        try {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            async Task<HttpStatusCode> Post(string user, string json) {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/greet") {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (user.Length > 0) request.Headers.Add("x-user", user);
                return (await client.SendAsync(request, ct)).StatusCode;
            }

            (await Post("", MalformedParams)).Should().Be(HttpStatusCode.Unauthorized);
            (await Post("", ValidParams)).Should().Be(HttpStatusCode.Unauthorized);
            (await Post("member", MalformedParams)).Should().Be(HttpStatusCode.Forbidden);
            (await Post("admin", MalformedParams)).Should().Be(HttpStatusCode.BadRequest);
            (await Post("admin", ValidParams)).Should().Be(HttpStatusCode.OK);
        }
        finally {
            await app.StopAsync(ct);
        }
    }

    [Fact]
    public async Task Gate_AgreesWithTheFullAuthorizer_ForDeclaredRequirements() {
        var requirements = new AuthorizationRequirements(false, false, ["p"], ["r"], [], [], []);
        var authorizer = new ClaimsAuthorizer(
            Member, [], new StubResourceAuthorizer(), new AuthorizationOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimsAuthorizer>.Instance);

        var gate = await authorizer.AuthorizeGateAsync(requirements, TestContext.Current.CancellationToken);
        var full = await authorizer.AuthorizeAsync(requirements, new object(), TestContext.Current.CancellationToken);

        gate!.Kind.Should().Be(ErrorKind.Forbidden);
        full!.Kind.Should().Be(gate.Kind);
    }

    [Fact]
    public async Task Gate_SkipsPoliciesAndRules_BecauseTheyNeedThePayload() {
        var requirements = new AuthorizationRequirements(false, true, [], [], [], ["NoSuchPolicy"], []);
        var authorizer = new ClaimsAuthorizer(
            Member, [], new StubResourceAuthorizer(), new AuthorizationOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ClaimsAuthorizer>.Instance);

        (await authorizer.AuthorizeGateAsync(requirements, TestContext.Current.CancellationToken)).Should().BeNull();
        (await authorizer.AuthorizeAsync(requirements, new object(), TestContext.Current.CancellationToken))
            .Should().NotBeNull();
    }
}
