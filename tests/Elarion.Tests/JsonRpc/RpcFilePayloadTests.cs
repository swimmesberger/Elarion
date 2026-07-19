using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.JsonRpc;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.JsonRpc;

/// <summary>
/// File payloads over the name-routed transport: a <c>Result&lt;ElarionFile&gt;</c> handler serializes its result
/// as the canonical base64 envelope, and an <see cref="ElarionFile"/> request property (an upload) deserializes
/// from the same envelope — both directions through the ordinary dispatcher path, no special-casing.
/// </summary>
public sealed class RpcFilePayloadTests {
    private sealed record DownloadQuery {
        public required string Name { get; init; }
    }

    private sealed class DownloadHandler : IHandler<DownloadQuery, Result<ElarionFile>> {
        public ValueTask<Result<ElarionFile>> HandleAsync(DownloadQuery request, CancellationToken ct) {
            return ValueTask.FromResult<Result<ElarionFile>>(
                new ElarionFile("id;name"u8.ToArray(), "text/csv") { FileName = $"{request.Name}.csv" });
        }
    }

    private sealed record UploadCommand {
        public required string Container { get; init; }
        public required ElarionFile File { get; init; }
    }

    private sealed record UploadReceipt(string Container, string ContentType, int Size);

    private sealed class UploadHandler : IHandler<UploadCommand, Result<UploadReceipt>> {
        public ValueTask<Result<UploadReceipt>> HandleAsync(UploadCommand request, CancellationToken ct) {
            return ValueTask.FromResult<Result<UploadReceipt>>(new UploadReceipt(
                request.Container, request.File.ContentType, request.File.Bytes.Length));
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    private static (JsonRpcDispatcher Dispatcher, IServiceProvider Services) Build() {
        var dispatcher = new JsonRpcDispatcher(Options)
            .Map<DownloadQuery, ElarionFile>("files.download")
            .Map<UploadCommand, UploadReceipt>("files.upload")
            .Freeze();

        var services = new ServiceCollection()
            .AddScoped<IHandler<DownloadQuery, Result<ElarionFile>>, DownloadHandler>()
            .AddScoped<IHandler<UploadCommand, Result<UploadReceipt>>, UploadHandler>()
            .BuildServiceProvider();

        return (dispatcher, services);
    }

    [Fact]
    public async Task Dispatch_FileResponse_SerializesAsBase64Envelope() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "files.download",
            Params = JsonSerializer.SerializeToElement(new { name = "clients" }, Options),
            Id = "1"
        };

        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        var file = response.Result.Should().BeOfType<ElarionFile>().Subject;

        // The wire shape is the converter's fixed envelope — what JsonRpcResponse serialization produces.
        var wire = JsonSerializer.Serialize(file, Options.GetTypeInfo(typeof(ElarionFile)));
        wire.Should().Contain("\"contentType\":\"text/csv\"");
        wire.Should().Contain("\"fileName\":\"clients.csv\"");
        wire.Should().Contain($"\"data\":\"{Convert.ToBase64String("id;name"u8.ToArray())}\"");
    }

    [Fact]
    public async Task Dispatch_FileInParams_DeserializesUpload() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        // Built the way a client would send it: the file property is the converter's base64 envelope.
        var upload = new {
            container = "invoices",
            file = new ElarionFile("hello upload"u8.ToArray(), "text/plain") { FileName = "note.txt" }
        };
        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "files.upload",
            Params = JsonSerializer.SerializeToElement(upload, Options),
            Id = "1"
        };

        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().BeNull();
        var receipt = response.Result.Should().BeOfType<UploadReceipt>().Subject;
        receipt.Container.Should().Be("invoices");
        receipt.ContentType.Should().Be("text/plain");
        receipt.Size.Should().Be("hello upload"u8.Length);
    }

    [Fact]
    public async Task Dispatch_MalformedFileData_ReturnsInvalidParams() {
        var (dispatcher, services) = Build();
        await using var scope = services.CreateAsyncScope();

        // A client typo in the base64 payload is invalid input (-32602), never an internal error (-32603).
        var request = new JsonRpcRequest {
            Jsonrpc = "2.0",
            Method = "files.upload",
            Params = JsonDocument.Parse(
                """{"container":"invoices","file":{"contentType":"text/plain","data":"not base64!!!"}}""").RootElement,
            Id = "1"
        };

        var response = await dispatcher.DispatchAsync(
            request, scope.ServiceProvider, TestContext.Current.CancellationToken);

        response.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(-32602);
    }
}
