using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Elarion.WebPush;

namespace Elarion.Tests.WebPush;

/// <summary>
/// A browser's side of a push subscription: owns the P-256 key pair and auth secret, produces the
/// serialized subscription, and decrypts what the server sent (the RFC 8291 receiver).
/// </summary>
internal sealed class TestPushSubscriber : IDisposable {
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _auth = RandomNumberGenerator.GetBytes(16);

    public TestPushSubscriber(string endpoint) {
        Endpoint = endpoint;
        var q = _key.ExportParameters(false).Q;
        PublicKey = [0x04, .. q.X!, .. q.Y!];
    }

    public string Endpoint { get; }

    public byte[] PublicKey { get; }

    public string P256dh => Base64Url.EncodeToString(PublicKey);

    public string Auth => Base64Url.EncodeToString(_auth);

    public void Dispose() {
        _key.Dispose();
    }

    public PushSubscriptionRequest ToRequest() {
        return new PushSubscriptionRequest {
            Endpoint = Endpoint, Keys = new PushSubscriptionKeys { P256dh = P256dh, Auth = Auth }
        };
    }

    public PushSubscription ToSubscription(string userId) {
        return new PushSubscription {
            Endpoint = Endpoint,
            P256dh = P256dh,
            Auth = Auth,
            UserId = userId,
            CreatedAt = DateTimeOffset.UnixEpoch,
            LastSeenAt = DateTimeOffset.UnixEpoch
        };
    }

    public byte[] Decrypt(byte[] body) {
        return Decrypt(body, _key, PublicKey, _auth);
    }

    public static byte[] Decrypt(byte[] body, ECDiffieHellman receiverKey, byte[] receiverPublicKey, byte[] auth) {
        var salt = body.AsSpan(0, 16);
        var recordSize = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4));
        if (recordSize != 4096) throw new CryptographicException("Unexpected record size.");
        var idLength = body[20];
        var senderPublicKey = body.AsSpan(21, idLength).ToArray();
        var ciphertext = body.AsSpan(21 + idLength);

        using var sender = ECDiffieHellman.Create(new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = senderPublicKey[1..33], Y = senderPublicKey[33..65] }
        });
        var ecdhSecret = receiverKey.DeriveRawSecretAgreement(sender.PublicKey);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. receiverPublicKey, .. senderPublicKey];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, auth, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt.ToArray());
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        var plaintext = new byte[ciphertext.Length - 16];
        using var aes = new AesGcm(cek, 16);
        aes.Decrypt(nonce, ciphertext[..^16], ciphertext[^16..], plaintext);
        // Strip the RFC 8188 padding: trailing zeros, then the 0x02 last-record delimiter.
        var end = Array.FindLastIndex(plaintext, b => b != 0);
        if (end < 0 || plaintext[end] != 0x02) throw new CryptographicException("Missing record delimiter.");
        return plaintext[..end];
    }
}

/// <summary>A captured push-service request.</summary>
internal sealed record RecordedPushRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body);

/// <summary>
/// Stands in for the push services: records every request and answers with the status the test chose for
/// that endpoint (201 Created by default), or throws to simulate a network failure.
/// </summary>
internal sealed class FakePushService {
    private readonly ConcurrentDictionary<string, Func<HttpResponseMessage>> _responses = new();

    public ConcurrentQueue<RecordedPushRequest> Requests { get; } = new();

    public void Respond(string endpoint, HttpStatusCode status) {
        _responses[endpoint] = () => new HttpResponseMessage(status);
    }

    public void Throw(string endpoint) {
        _responses[endpoint] = () => throw new HttpRequestException("connection refused");
    }

    public HttpMessageHandler CreateHandler() {
        return new Handler(this);
    }

    private sealed class Handler(FakePushService service) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) {
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value),
                    StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            service.Requests.Enqueue(new RecordedPushRequest(request.Method, request.RequestUri!, headers, body));
            return service._responses.TryGetValue(request.RequestUri!.ToString(), out var respond)
                ? respond()
                : new HttpResponseMessage(HttpStatusCode.Created);
        }
    }
}
