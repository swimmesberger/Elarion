using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.JsonRpc;

public sealed class ErrorContractSchemaTests {
    private static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public sealed record TokenProblem(string Reason, int? Attempt);

    public sealed record Ping(string Message);

    public sealed record Pong(string Message);

    private static JsonObject Export(
        Action<JsonRpcDispatcher> register, JsonSerializerOptions? options = null) {
        var dispatcher = new JsonRpcDispatcher(options ?? Options);
        register(dispatcher);
        dispatcher.Freeze();
        return JsonNode.Parse(JsonRpcSchemaExporter.Generate(dispatcher, options ?? Options))!.AsObject();
    }

    private static Result<Pong> Ok(Ping _, IServiceProvider __, CancellationToken ___) =>
        new Pong("x");

    [Fact]
    public void Schema_ListsDeclaredErrors_ByCode_WithKindAndPayloadSchema() {
        var schema = Export(d => d.MapDelegate<Ping, Pong>(
            "t.ping",
            (r, sp, ct) => ValueTask.FromResult(Ok(r, sp, ct)),
            errors: [
                new ErrorContract { Code = "token.malformed", Kind = ErrorKind.Validation, DataType = typeof(TokenProblem) },
                new ErrorContract { Code = "not_found", Kind = ErrorKind.NotFound }
            ]));

        var errors = schema["methods"]!["t.ping"]!["errors"]!.AsObject();
        errors.Select(e => e.Key).Should().Equal("not_found", "token.malformed");
        errors["not_found"]!["kind"]!.GetValue<string>().Should().Be("not_found");
        errors["not_found"]!.AsObject().ContainsKey("data").Should().BeFalse();
        errors["token.malformed"]!["kind"]!.GetValue<string>().Should().Be("validation");
        var data = errors["token.malformed"]!["data"]!;
        data["required"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("reason");
        data["properties"]!.AsObject().ContainsKey("attempt").Should().BeTrue();
    }

    [Fact]
    public void Schema_OmitsErrors_WhenNoneDeclared() {
        var schema = Export(d => d.MapDelegate<Ping, Pong>("t.ping", (r, sp, ct) => ValueTask.FromResult(Ok(r, sp, ct))));

        schema["methods"]!["t.ping"]!.AsObject().ContainsKey("errors").Should().BeFalse();
    }

    [Fact]
    public void Schema_IsDeterministic_RegardlessOfDeclarationOrder() {
        ErrorContract A() => new() { Code = "a.one", Kind = ErrorKind.Conflict };
        ErrorContract B() => new() { Code = "b.two", Kind = ErrorKind.Conflict };
        string Gen(IReadOnlyList<ErrorContract> errors) => Export(d => d.MapDelegate<Ping, Pong>(
            "t.ping", (r, sp, ct) => ValueTask.FromResult(Ok(r, sp, ct)), errors: errors)).ToJsonString();

        Gen([A(), B()]).Should().Be(Gen([B(), A()]));
    }

    private sealed record RequestShapes {
        public required string Name { get; init; }
        public required string? Note { get; init; }
        public string Plain { get; init; } = "";
        public int Page { get; init; } = 1;
        public int Count { get; init; }
        public string? Optional { get; init; }
        public List<string> Tags { get; init; } = [];
    }

    private sealed record CtorRequest(string Name, int Size = 10, string? Memo = null);

    private sealed record ResponseShapes {
        public required string Id { get; init; }
        public required string? MaybeRequired { get; init; }
        public string Plain { get; init; } = "";
        public int Count { get; init; }
        public string? Optional { get; init; }
    }

    private static string[] Required(JsonNode? node) =>
        node?["required"]?.AsArray().Select(n => n!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray() ?? [];

    [Fact]
    public void Request_NonNullableIsRequired_NullableAndDefaultedAreOptional() {
        var schema = Export(d => d.MapDelegate<RequestShapes, Pong>(
            "t.shapes", (_, _, _) => ValueTask.FromResult<Result<Pong>>(new Pong("x"))));

        // Name and Count (an initializer equal to the CLR default is indistinguishable from none) are required;
        // Note is nullable although declared `required`; Plain/Page/Tags have non-default initializers.
        Required(schema["methods"]!["t.shapes"]!["params"]).Should().Equal("count", "name");
    }

    [Fact]
    public void Request_ConstructorDefaultsAreOptional_ToSend() {
        var schema = Export(d => d.MapDelegate<CtorRequest, Pong>(
            "t.ctor", (_, _, _) => ValueTask.FromResult<Result<Pong>>(new Pong("x"))));

        Required(schema["methods"]!["t.ctor"]!["params"]).Should().Equal("name");
    }

    [Fact]
    public void Response_NonNullableIsRequired_NullableIsOptional_RegardlessOfRequiredModifier() {
        var schema = Export(d => d.MapDelegate<Ping, ResponseShapes>(
            "t.resp", (_, _, _) => ValueTask.FromResult<Result<ResponseShapes>>(new ResponseShapes { Id = "1", MaybeRequired = null })));

        Required(schema["methods"]!["t.resp"]!["result"]).Should().Equal("count", "id", "plain");
    }

    private sealed record IgnoringResponse {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public int Count { get; init; }
    }

    private sealed record NullableIgnoringResponse {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Note { get; init; }
    }

    [Fact]
    public void Response_ConditionalWriteOfNonNullableMember_FailsTheExport() {
        var act = () => Export(d => d.MapDelegate<Ping, IgnoringResponse>(
            "t.bad", (_, _, _) => ValueTask.FromResult<Result<IgnoringResponse>>(new IgnoringResponse())));

        act.Should().Throw<InvalidOperationException>().WithMessage("*'Count'*non-nullable*WhenWritingDefault*");
    }

    [Fact]
    public void Response_ConditionalWriteOfNullableMember_IsFine() {
        var schema = Export(d => d.MapDelegate<Ping, NullableIgnoringResponse>(
            "t.ok", (_, _, _) => ValueTask.FromResult<Result<NullableIgnoringResponse>>(new NullableIgnoringResponse())));

        Required(schema["methods"]!["t.ok"]!["result"]).Should().BeEmpty();
    }

    [Fact]
    public void Response_GlobalWhenWritingDefault_FailsTheExport() {
        var options = new JsonSerializerOptions(Options) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault };

        var act = () => Export(d => d.MapDelegate<Ping, Pong>(
            "t.ping", (r, sp, ct) => ValueTask.FromResult(Ok(r, sp, ct))), options);

        act.Should().Throw<InvalidOperationException>().WithMessage("*WhenWritingDefault*");
    }

    private sealed class RecordingMonitor : IErrorContractMonitor {
        public List<(string Operation, string Code, ErrorContract? Declared)> Violations { get; } = [];

        public void Violation(string operation, AppError error, ErrorContract? declared) {
            Violations.Add((operation, error.Code, declared));
        }
    }

    private static async Task<RecordingMonitor> Invoke(
        IReadOnlyList<ErrorContract>? errors, AppError failure) {
        var dispatcher = new HandlerDispatcher().MapDelegate<Ping, Pong>(
            "t.ping", (_, _, _) => ValueTask.FromResult<Result<Pong>>(failure), errors: errors).Freeze();
        var monitor = new RecordingMonitor();
        var services = new ServiceCollection().AddSingleton<IErrorContractMonitor>(monitor).BuildServiceProvider();

        await dispatcher.DispatchAsync("t.ping", new Ping("x"), services, TestContext.Current.CancellationToken);
        return monitor;
    }

    [Fact]
    public async Task Runtime_UndeclaredCode_IsReported() {
        var monitor = await Invoke([], AppError.Conflict("c", "seat.taken"));

        monitor.Violations.Should().ContainSingle().Which.Should().Match<(string, string, ErrorContract?)>(
            v => v.Item1 == "t.ping" && v.Item2 == "seat.taken" && v.Item3 == null);
    }

    [Fact]
    public async Task Runtime_DeclaredCode_IsNotReported() {
        var monitor = await Invoke(
            [new ErrorContract { Code = "seat.taken", Kind = ErrorKind.Conflict }], AppError.Conflict("c", "seat.taken"));

        monitor.Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Runtime_DeclaredCodeWithDifferentKind_IsReportedAsMismatch() {
        var monitor = await Invoke(
            [new ErrorContract { Code = "seat.taken", Kind = ErrorKind.Conflict }],
            AppError.BusinessRule("c", "seat.taken"));

        monitor.Violations.Should().ContainSingle().Which.Declared.Should().NotBeNull();
    }

    [Fact]
    public async Task Runtime_InternalAndPipelineAuthCodes_AreAlwaysAdmitted() {
        (await Invoke([], AppError.Internal("boom"))).Violations.Should().BeEmpty();
        (await Invoke([], AppError.Unauthorized("u"))).Violations.Should().BeEmpty();
        (await Invoke([], AppError.Forbidden("f"))).Violations.Should().BeEmpty();
    }

    [Fact]
    public async Task Runtime_HandWiredRouteWithoutContract_IsNotChecked() {
        (await Invoke(null, AppError.Conflict("c", "seat.taken"))).Violations.Should().BeEmpty();
    }

    [Fact]
    public void Monitor_OutsideDevelopment_DoesNothing_AndInDevelopmentLogsEachViolationOnce() {
        var logs = new List<string>();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(new ListLoggerProvider(logs)))
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeEnvironment("Development"))
            .AddElarionErrorContractMonitor()
            .BuildServiceProvider();

        var monitor = services.GetRequiredService<IErrorContractMonitor>();
        monitor.Violation("op", AppError.Conflict("c", "a.b"), null);
        monitor.Violation("op", AppError.Conflict("c", "a.b"), null);
        logs.Should().ContainSingle().Which.Should().Contain("a.b");

        var prodLogs = new List<string>();
        var prod = new ServiceCollection()
            .AddLogging(b => b.AddProvider(new ListLoggerProvider(prodLogs)))
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeEnvironment("Production"))
            .AddElarionErrorContractMonitor()
            .BuildServiceProvider();
        prod.GetRequiredService<IErrorContractMonitor>().Violation("op", AppError.Conflict("c", "a.b"), null);
        prodLogs.Should().BeEmpty();
    }

    private sealed class FakeEnvironment(string name) : Microsoft.Extensions.Hosting.IHostEnvironment {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink);

        public void Dispose() {
        }

        private sealed class ListLogger(List<string> sink) : ILogger {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) {
                sink.Add(formatter(state, exception));
            }
        }
    }
}
