using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Elarion.Tests.AspNetCore;

/// <summary>
/// The streamed file tier over a real Kestrel host (which refuses synchronous body writes), mapped exactly as the
/// generator emits a <c>Result&lt;ElarionStreamedFile&gt;</c> endpoint: the file's disposition, entity tag, caching and
/// sniffing headers, a <c>304</c> for a matching <c>If-None-Match</c>, an archive written with the async
/// <see cref="ZipArchive"/> APIs, an aborted connection for a failure after the first byte, and disposal of the file
/// with the response.
/// </summary>
public sealed class StreamedFileEndToEndTests {
    private sealed record ExportQuery {
        public required string Kind { get; init; }
    }

    private sealed class DisposalProbe : IAsyncDisposable {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask DisposeAsync() {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ExportHandler(DisposalProbe probe) : IHandler<ExportQuery, Result<ElarionStreamedFile>> {
        public ValueTask<Result<ElarionStreamedFile>> HandleAsync(ExportQuery request, CancellationToken ct) {
            Result<ElarionStreamedFile> result = request.Kind switch {
                "stored" => ElarionStreamedFile.FromStream(
                    new MemoryStream("%PDF-stored"u8.ToArray()), "application/pdf", probe) with {
                    FileName = "Übersicht 2026.pdf",
                    EntityTag = "a1b2c3",
                    LastModified = new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero),
                    CacheControl = "private, max-age=3600"
                },
                "inline" => ElarionStreamedFile.FromStream(new MemoryStream([1, 2, 3]), "image/png") with {
                    FileName = "photo.png",
                    Disposition = FileDisposition.Inline
                },
                "zip" => ElarionStreamedFile.FromWriter(WriteArchiveAsync, "application/zip") with {
                    FileName = "documents.zip"
                },
                "broken" => ElarionStreamedFile.FromWriter(async (body, ct) => {
                    await body.WriteAsync(new byte[64 * 1024], ct);
                    await body.FlushAsync(ct);
                    throw new InvalidOperationException("The third document vanished.");
                }, "application/zip"),
                // Name, type and tag are known only once the content is read; so is whether the part exists.
                "deferred" => ElarionStreamedFile.Deferred(async ct => {
                    await Task.Yield();
                    return ElarionStreamedFile.FromStream(
                        new MemoryStream("attachment"u8.ToArray()), "text/calendar", probe) with {
                        FileName = "invite.ics",
                        EntityTag = "part-2"
                    };
                }),
                "deferred-missing" => ElarionStreamedFile.Deferred(async ct => {
                    await Task.Yield();
                    return AppError.NotFound("No such attachment.", "mail.attachment_not_found");
                }),
                // A forward-only content (a decoded part) whose size is known before it is read.
                "sized" => ElarionStreamedFile.FromStream(
                    new ForwardOnlyStream(new byte[100_000]), "application/octet-stream") with {
                    Length = 100_000,
                    EntityTag = "sized-1"
                },
                "sized-writer" => ElarionStreamedFile.FromWriter(
                    (body, ct) => body.WriteAsync(new byte[70_000], ct).AsTask(), "application/octet-stream") with {
                    Length = 70_000
                },
                "sized-short" => ElarionStreamedFile.FromWriter(async (body, ct) => {
                    await body.WriteAsync(new byte[64 * 1024], ct);
                    await body.FlushAsync(ct);
                }, "application/octet-stream") with {
                    Length = 100_000
                },
                "sized-long" => ElarionStreamedFile.FromStream(
                    new ForwardOnlyStream(new byte[100_000]), "application/octet-stream") with {
                    Length = 1_000
                },
                "sized-seekable-wrong" => ElarionStreamedFile.FromStream(
                    new MemoryStream(new byte[10]), "application/octet-stream") with {
                    Length = 11
                },
                _ => AppError.NotFound("No such export.")
            };
            return ValueTask.FromResult(result);
        }
    }

    // The fully async archive: ZipArchiveEntry's entry stream still flushes its trailer through the synchronous
    // Dispose, which the response body must tolerate without AllowSynchronousIO.
    private static async Task WriteArchiveAsync(Stream body, CancellationToken ct) {
        await using var archive = await ZipArchive.CreateAsync(body, ZipArchiveMode.Create, true, null, ct);
        for (var index = 0; index < 3; index++) {
            var entry = archive.CreateEntry($"document-{index}.txt", CompressionLevel.Fastest);
            await using var content = await entry.OpenAsync(ct);
            await content.WriteAsync(System.Text.Encoding.UTF8.GetBytes(new string((char)('a' + index), 100_000)), ct);
        }
    }

    [Fact]
    public async Task StoredFile_CarriesItsHeaders_AnswersAMatchingTagWith304_AndIsDisposed() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/stored", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        response.Content.Headers.ContentDisposition.FileNameStar.Should().Be("Übersicht 2026.pdf");
        response.Headers.ETag!.Tag.Should().Be("\"a1b2c3\"");
        response.Headers.ETag.IsWeak.Should().BeFalse();
        response.Content.Headers.LastModified.Should().Be(new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().Be(TimeSpan.FromHours(1));
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await response.Content.ReadAsStringAsync(ct)).Should().Be("%PDF-stored");
        await host.Probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/exports/stored");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"a1b2c3\""));
        var notModified = await host.Client.SendAsync(conditional, ct);

        notModified.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await notModified.Content.ReadAsByteArrayAsync(ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task InlineFile_IsShownInPlaceWithItsName() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/inline", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("inline");
        response.Content.Headers.ContentDisposition.FileName.Should().Be("photo.png");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await response.Content.ReadAsByteArrayAsync(ct)).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Archive_WrittenWithTheAsyncZipApis_StreamsWithoutSynchronousIo() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/zip", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("documents.zip");
        await using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(ct), ZipArchiveMode.Read);
        archive.Entries.Select(entry => entry.FullName)
            .Should().Equal("document-0.txt", "document-1.txt", "document-2.txt");
        using var reader = new StreamReader(await archive.Entries[2].OpenAsync(ct));
        (await reader.ReadToEndAsync(ct)).Should().Be(new string('c', 100_000));
    }

    [Fact]
    public async Task FailureAfterTheFirstByte_AbortsTheConnection() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var download = async () => await host.Client.GetByteArrayAsync("/exports/broken", ct);

        await download.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Failure_IsAProblemResponse() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/none", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task DeferredFile_ServesWhatItsOpenAnswers_WithTheOpenedFilesHeaders_AndDisposesIt() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/deferred", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/calendar");
        response.Content.Headers.ContentDisposition!.FileName.Should().Be("invite.ics");
        response.Headers.ETag!.Tag.Should().Be("\"part-2\"");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        (await response.Content.ReadAsStringAsync(ct)).Should().Be("attachment");
        await host.Probe.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/exports/deferred");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"part-2\""));
        (await host.Client.SendAsync(conditional, ct)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task DeferredFile_RefusedByItsOpen_IsAProblemResponse_BeforeAnyByte() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/deferred-missing", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        response.Content.Headers.ContentDisposition.Should().BeNull("no file header is sent for a refusal");
        (await response.Content.ReadAsStringAsync(ct)).Should().Contain("mail.attachment_not_found");
    }

    [Fact]
    public async Task DeferredFile_InProcess_IsOpenedByTheCaller() {
        var ct = TestContext.Current.CancellationToken;
        var plain = ElarionStreamedFile.FromStream(new MemoryStream([1]), "text/plain");
        var deferred = ElarionStreamedFile.Deferred(_ => ValueTask.FromResult<Result<ElarionStreamedFile>>(plain));
        var nested = ElarionStreamedFile.Deferred(_ => ValueTask.FromResult<Result<ElarionStreamedFile>>(deferred));

        plain.IsDeferred.Should().BeFalse();
        (await plain.OpenAsync(ct)).Value.Should().BeSameAs(plain);
        deferred.IsDeferred.Should().BeTrue();
        (await deferred.OpenAsync(ct)).Value.Should().BeSameAs(plain);

        var write = () => deferred.WriteToAsync(new MemoryStream(), ct);
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*OpenAsync*");
        var openNested = async () => await nested.OpenAsync(ct);
        await openNested.Should().ThrowAsync<InvalidOperationException>().WithMessage("*another deferred file*");
    }

    [Fact]
    public void EntityTag_RejectsWhatAnETagCannotCarry() {
        var file = ElarionStreamedFile.FromStream(new MemoryStream(), "text/plain");

        foreach (var invalid in new[] { "", "with space", "with\"quote", "ümlaut" }) {
            var act = () => file with { EntityTag = invalid };
            act.Should().Throw<ArgumentException>();
        }

        (file with { EntityTag = "sha-256:0a1B" }).EntityTag.Should().Be("sha-256:0a1B");
    }

    [Fact]
    public async Task WriterBody_HoldsSynchronousWritesUntilTheNextAsynchronousOne_UpToItsLimit() {
        var ct = TestContext.Current.CancellationToken;
        var server = new MemoryStream();
        var body = new SynchronousWriteBufferingStream(server, 8);

        body.Write([1, 2, 3]);
        body.Flush();
        server.Length.Should().Be(0);
        await body.WriteAsync(new byte[] { 4 }, ct);
        server.ToArray().Should().Equal(1, 2, 3, 4);

        body.Write([5]);
        await body.FlushAsync(ct);
        server.ToArray().Should().Equal(1, 2, 3, 4, 5);

        var tooMuch = () => body.Write(new byte[9]);
        tooMuch.Should().Throw<InvalidOperationException>().WithMessage("*asynchronous*");
    }

    [Fact]
    public async Task ForwardOnlyContent_WithAStatedLength_IsSentWithContentLength() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/sized", HttpCompletionOption.ResponseHeadersRead, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentLength.Should().Be(100_000);
        response.Headers.TransferEncodingChunked.Should().NotBe(true);
        (await response.Content.ReadAsByteArrayAsync(ct)).Should().HaveCount(100_000);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/exports/sized");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"sized-1\""));
        (await host.Client.SendAsync(conditional, ct)).StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    [Fact]
    public async Task Writer_WithAStatedLength_IsSentWithContentLength() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/sized-writer", HttpCompletionOption.ResponseHeadersRead, ct);

        response.Content.Headers.ContentLength.Should().Be(70_000);
        (await response.Content.ReadAsByteArrayAsync(ct)).Should().HaveCount(70_000);
    }

    [Theory]
    [InlineData("sized-short")]
    [InlineData("sized-long")]
    public async Task StatedLength_TheContentDoesNotMatch_AbortsTheConnection(string kind) {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var download = async () => await host.Client.GetByteArrayAsync($"/exports/{kind}", ct);

        await download.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task StatedLength_DisagreeingWithASeekableStream_FailsBeforeAnyByte() {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await StartAsync(ct);

        var response = await host.Client.GetAsync("/exports/sized-seekable-wrong", ct);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Fact]
    public void Length_RejectsANegativeValue() {
        var file = ElarionStreamedFile.FromStream(new MemoryStream(), "text/plain");

        var act = () => file with { Length = -1 };

        act.Should().Throw<ArgumentOutOfRangeException>();
        (file with { Length = 0 }).Length.Should().Be(0);
    }

    [Fact]
    public async Task LengthCheckingBody_FailsPastTheLength_AndWhenShort() {
        var ct = TestContext.Current.CancellationToken;
        var body = new LengthCheckingStream(new MemoryStream(), 4);

        await body.WriteAsync(new byte[] { 1, 2, 3 }, ct);
        var incomplete = () => body.EnsureComplete();
        incomplete.Should().Throw<InvalidOperationException>().WithMessage("*wrote 3*");

        var tooMuch = async () => await body.WriteAsync(new byte[] { 4, 5 }, ct);
        await tooMuch.Should().ThrowAsync<InvalidOperationException>().WithMessage("*writes more*");

        await body.WriteAsync(new byte[] { 4 }, ct);
        body.EnsureComplete();
    }

    private sealed class ForwardOnlyStream(byte[] content) : Stream {
        private readonly MemoryStream _inner = new(content);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) {
            return _inner.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
            return _inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() {
        }

        public override long Seek(long offset, SeekOrigin origin) {
            throw new NotSupportedException();
        }

        public override void SetLength(long value) {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException();
        }
    }

    private static async Task<TestHost> StartAsync(CancellationToken ct) {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        var probe = new DisposalProbe();
        builder.Services.AddSingleton(probe);
        builder.Services.AddScoped<IHandler<ExportQuery, Result<ElarionStreamedFile>>, ExportHandler>();

        var app = builder.Build();
        // Mirrors the registration AppModuleDiscoveryGenerator emits for a Result<ElarionStreamedFile> handler.
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
                        .GetRequiredService<IHandler<ExportQuery, Result<ElarionStreamedFile>>>();
                    var __result = ElarionHttpResults.ToStreamedFileResult(
                        await __handler.HandleAsync(__request, __context.RequestAborted));
                    await __result.ExecuteAsync(__context);
                }))
            .WithMetadata(new ProducesResponseTypeMetadata(200, null, new[] { "application/octet-stream" }))
            .ProducesElarionErrors()
            .WithMetadata(ElarionFileEndpointMetadata.Instance);

        await app.StartAsync(ct);
        var baseAddress = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new TestHost(app, new HttpClient { BaseAddress = new Uri(baseAddress) }, probe);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, DisposalProbe probe) : IAsyncDisposable {
        public HttpClient Client => client;

        public DisposalProbe Probe => probe;

        public async ValueTask DisposeAsync() {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
