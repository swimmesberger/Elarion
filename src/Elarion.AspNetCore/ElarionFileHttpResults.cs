using Elarion.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Elarion.AspNetCore;

/// <summary>What every file response of the framework carries, whatever produced its bytes.</summary>
internal static class ElarionFileResponseHeaders {
    /// <summary>
    /// <c>X-Content-Type-Options: nosniff</c>: a file is often somebody's upload served from the application's own
    /// origin, so the browser must take its declared content type and never sniff the bytes into one it renders.
    /// </summary>
    public static void ForbidSniffing(HttpResponse response) {
        response.Headers.XContentTypeOptions = "nosniff";
    }
}

/// <summary>An <see cref="ElarionFile"/> response: the ASP.NET bytes result, with the framework's file headers.</summary>
internal sealed class ElarionFileHttpResult(IResult inner) : IResult {
    /// <summary>The ASP.NET result that writes the bytes.</summary>
    public IResult Inner => inner;

    public Task ExecuteAsync(HttpContext httpContext) {
        ArgumentNullException.ThrowIfNull(httpContext);
        ElarionFileResponseHeaders.ForbidSniffing(httpContext.Response);
        return inner.ExecuteAsync(httpContext);
    }
}

/// <summary>
/// An <see cref="ElarionStreamedFile"/> response. ASP.NET's stream results write the content and answer conditional
/// requests (<c>If-None-Match</c>, <c>If-Modified-Since</c>); this adds the disposition, caching and sniffing headers,
/// ties the file's lifetime to the response, and aborts the connection when the content fails mid-stream.
/// </summary>
internal sealed class ElarionStreamedFileHttpResult(ElarionStreamedFile file) : IResult {
    /// <summary>The largest run of synchronous writes a writer's body holds before the next asynchronous write.</summary>
    internal const int SynchronousWriteLimit = 1024 * 1024;

    public ElarionStreamedFile File => file;

    public async Task ExecuteAsync(HttpContext httpContext) {
        ArgumentNullException.ThrowIfNull(httpContext);
        httpContext.Response.RegisterForDisposeAsync(file);

        var headers = httpContext.Response.Headers;
        ElarionFileResponseHeaders.ForbidSniffing(httpContext.Response);
        var disposition = new ContentDispositionHeaderValue(
            file.Disposition == FileDisposition.Inline ? "inline" : "attachment");
        // SetHttpFileName sends the name as UTF-8 (filename*) with a sanitized ASCII fallback (filename).
        if (file.FileName is { Length: > 0 } fileName) disposition.SetHttpFileName(fileName);
        headers.ContentDisposition = disposition.ToString();
        if (file.CacheControl is { } cacheControl) headers.CacheControl = cacheControl;

        var entityTag = file.EntityTag is { } tag ? new EntityTagHeaderValue($"\"{tag}\"") : null;
        var cancellationToken = httpContext.RequestAborted;
        // The file name is null on purpose: the disposition above is the file's own (inline or attachment), which the
        // ASP.NET results would overwrite with an attachment.
        IResult inner = file.Content is { } content
            ? TypedResults.Stream(content, file.ContentType, null, file.LastModified, entityTag)
            : TypedResults.Stream(
                body => WriteAsync(file, body, cancellationToken), file.ContentType, null, file.LastModified, entityTag);

        try {
            await inner.ExecuteAsync(httpContext).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // The client went away: a normal end of a download, not a failure.
        }
        catch (Exception exception) when (httpContext.Response.HasStarted) {
            // The status and part of the file are sent; completing the response would present a truncated file as a
            // complete one. Aborting makes the failure visible to the client.
            httpContext.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger(typeof(ElarionStreamedFileHttpResult))
                .LogError(exception, "The streamed file {FileName} failed after its first byte; the connection is aborted.",
                    file.FileName);
            httpContext.Abort();
        }
    }

    private static async Task WriteAsync(ElarionStreamedFile file, Stream body, CancellationToken cancellationToken) {
        var tolerant = new SynchronousWriteBufferingStream(body, SynchronousWriteLimit);
        await file.WriteToAsync(tolerant, cancellationToken).ConfigureAwait(false);
        await tolerant.FlushPendingAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The response body a streamed file's writer receives. A server body refuses synchronous writes (Kestrel's
/// <c>AllowSynchronousIO</c> is off), but some writers make a few while disposing — an entry stream of
/// <see cref="System.IO.Compression.ZipArchive"/> flushes its compressed trailer and data descriptor through the
/// synchronous <c>Dispose</c> even when opened with <c>OpenAsync</c>. Those bytes are held here and sent with the next
/// asynchronous write or flush, so the writer needs no <c>AllowSynchronousIO</c> for the whole response. The hold is
/// bounded: a writer that writes everything synchronously fails at <c>limit</c> bytes instead of buffering the file.
/// </summary>
internal sealed class SynchronousWriteBufferingStream(Stream inner, int limit) : Stream {
    private byte[] _pending = [];
    private int _pendingCount;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) {
        ValidateBufferArguments(buffer, offset, count);
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer) {
        if (buffer.IsEmpty) return;

        var required = _pendingCount + buffer.Length;
        if (required > limit)
            throw new InvalidOperationException(
                $"The file writer wrote more than {limit} bytes synchronously; the response body accepts only asynchronous " +
                "writes. Use WriteAsync (and the async APIs of the library that writes, such as ZipArchive.CreateAsync and " +
                "ZipArchiveEntry.OpenAsync).");

        if (required > _pending.Length)
            Array.Resize(ref _pending, Math.Min(limit, Math.Max(required, Math.Max(256, _pending.Length * 2))));

        buffer.CopyTo(_pending.AsSpan(_pendingCount));
        _pendingCount = required;
    }

    public override void WriteByte(byte value) {
        Write([value]);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) {
        ValidateBufferArguments(buffer, offset, count);
        return WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) {
        await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    // Held bytes go out with the next asynchronous write or flush; a synchronous flush cannot send them.
    public override void Flush() {
    }

    public override async Task FlushAsync(CancellationToken cancellationToken) {
        await FlushPendingAsync(cancellationToken).ConfigureAwait(false);
        await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends the bytes held from synchronous writes.</summary>
    public async Task FlushPendingAsync(CancellationToken cancellationToken) {
        if (_pendingCount == 0) return;

        var count = _pendingCount;
        _pendingCount = 0;
        await inner.WriteAsync(_pending.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) {
        throw new NotSupportedException();
    }

    public override void SetLength(long value) {
        throw new NotSupportedException();
    }

    // The response body belongs to the server: disposing the writer's view of it (a writer's leaveOpen: false) only
    // keeps the held bytes for the final flush.
    protected override void Dispose(bool disposing) {
    }

    public override ValueTask DisposeAsync() {
        return ValueTask.CompletedTask;
    }
}
