using System.Net.WebSockets;
using AwesomeAssertions;
using Elarion.Connections.AspNetCore;
using Xunit;

namespace Elarion.Tests.Connections;

/// <summary>
/// The pooled WebSocket reassembly buffer (ADR-0066): multi-frame messages reassemble correctly, sequential
/// messages reuse one buffer (call-scoped payloads, zero allocation per message), the size cap still throws,
/// and growth beyond the initial rent is trimmed back so one oversized message never pins its footprint.
/// </summary>
public sealed class WebSocketMessageReaderTests {
    [Fact]
    public async Task ReadAsync_ReassemblesMultiFrameMessages() {
        using var socket = new ScriptedWebSocket([
            Frame("hel", endOfMessage: false),
            Frame("lo-", endOfMessage: false),
            Frame("world", endOfMessage: true)
        ]);
        using var reader = new WebSocketMessageReader(socket, maxMessageBytes: 1024, receiveBufferBytes: 4);

        var message = await reader.ReadAsync(TestContext.Current.CancellationToken);

        message.Should().NotBeNull();
        System.Text.Encoding.UTF8.GetString(message!.Value.Payload.Span).Should().Be("hello-world");
    }

    [Fact]
    public async Task ReadAsync_SequentialMessages_ReuseThePooledBuffer() {
        using var socket = new ScriptedWebSocket([
            Frame("first", endOfMessage: true),
            Frame("second", endOfMessage: true)
        ]);
        using var reader = new WebSocketMessageReader(socket, maxMessageBytes: 1024, receiveBufferBytes: 64);

        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        // Capture the call-scoped view's content before the next read invalidates it.
        var firstText = System.Text.Encoding.UTF8.GetString(first!.Value.Payload.Span);
        var second = await reader.ReadAsync(TestContext.Current.CancellationToken);

        firstText.Should().Be("first");
        System.Text.Encoding.UTF8.GetString(second!.Value.Payload.Span).Should().Be("second");
        // The call-scoped contract in action: both payloads view the same pooled backing buffer, so the
        // first message's memory was overwritten by the second read — retention would be a bug in the codec.
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(first.Value.Payload, out var firstSegment)
            .Should().BeTrue();
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(second.Value.Payload, out var secondSegment)
            .Should().BeTrue();
        secondSegment.Array.Should().BeSameAs(firstSegment.Array);
    }

    [Fact]
    public async Task ReadAsync_MessageOverTheCap_Throws() {
        using var socket = new ScriptedWebSocket([
            Frame(new string('x', 64), endOfMessage: false),
            Frame(new string('x', 64), endOfMessage: true)
        ]);
        using var reader = new WebSocketMessageReader(socket, maxMessageBytes: 100, receiveBufferBytes: 32);

        var read = async () => await reader.ReadAsync(TestContext.Current.CancellationToken);

        await read.Should().ThrowAsync<WebSocketMessageTooLargeException>();
    }

    [Fact]
    public async Task ReadAsync_CloseFrame_ReturnsNull() {
        using var socket = new ScriptedWebSocket([null]);
        using var reader = new WebSocketMessageReader(socket, maxMessageBytes: 1024, receiveBufferBytes: 32);

        (await reader.ReadAsync(TestContext.Current.CancellationToken)).Should().BeNull();
    }

    private static (byte[] Payload, bool EndOfMessage) Frame(string text, bool endOfMessage) {
        return (System.Text.Encoding.UTF8.GetBytes(text), endOfMessage);
    }

    /// <summary>A scripted server-side socket: each entry is one received frame; null is a close frame.</summary>
    private sealed class ScriptedWebSocket((byte[] Payload, bool EndOfMessage)?[] frames) : WebSocket {
        private int _index;
        private int _offset;

        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(
            Memory<byte> buffer, CancellationToken ct) {
            await Task.Yield();
            if (_index >= frames.Length || frames[_index] is not { } frame)
                return new ValueWebSocketReceiveResult(0, WebSocketMessageType.Close, true);

            var remaining = frame.Payload.Length - _offset;
            var count = Math.Min(buffer.Length, remaining);
            frame.Payload.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            var frameDone = _offset == frame.Payload.Length;
            if (frameDone) {
                _index++;
                _offset = 0;
            }

            return new ValueWebSocketReceiveResult(
                count, WebSocketMessageType.Binary, frameDone && frame.EndOfMessage);
        }

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer, CancellationToken ct) {
            throw new NotSupportedException();
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) {
            throw new NotSupportedException();
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) {
            throw new NotSupportedException();
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken ct) {
            throw new NotSupportedException();
        }

        public override void Abort() {
        }

        public override void Dispose() {
        }

        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
    }
}
