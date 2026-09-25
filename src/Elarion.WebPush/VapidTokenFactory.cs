using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;

namespace Elarion.WebPush;

/// <summary>
/// Mints the RFC 8292 <c>Authorization: vapid t=…, k=…</c> header: an ES256 JWT whose audience is the
/// push service origin. Tokens are cached per origin and reused until close to expiry, so a fan-out to
/// thousands of FCM subscriptions signs once, not thousands of times.
/// </summary>
internal sealed class VapidTokenFactory(TimeProvider timeProvider) {
    // Push services reject an exp more than 24h ahead; 12h leaves margin for clock skew.
    internal static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromHours(1);

    private static readonly string EncodedHeader = Base64Url.EncodeToString("""{"typ":"JWT","alg":"ES256"}"""u8);

    private readonly ConcurrentDictionary<CacheKey, CachedHeader> _cache = new();

    public string GetAuthorizationHeader(string audience, string subject, VapidKeys keys) {
        var now = timeProvider.GetUtcNow();
        var key = new CacheKey(audience, subject, keys.PublicKey);
        if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt - now > RenewBefore) return cached.Value;

        var expiresAt = now + TokenLifetime;
        var value = $"vapid t={CreateToken(audience, subject, expiresAt, keys)}, k={keys.PublicKey}";
        _cache[key] = new CachedHeader(value, expiresAt);
        return value;
    }

    internal static string CreateToken(string audience, string subject, DateTimeOffset expiresAt, VapidKeys keys) {
        var claims = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(claims)) {
            writer.WriteStartObject();
            writer.WriteString("aud", audience);
            writer.WriteNumber("exp", expiresAt.ToUnixTimeSeconds());
            writer.WriteString("sub", subject);
            writer.WriteEndObject();
        }

        var signingInput = $"{EncodedHeader}.{Base64Url.EncodeToString(claims.WrittenSpan)}";
        using var signer = keys.CreateSigner();
        // JWS ES256 is the raw r ‖ s concatenation (IEEE P1363), which SignData emits by default — not DER.
        var signature = signer.SignData(
            System.Text.Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private readonly record struct CacheKey(string Audience, string Subject, string PublicKey);

    private readonly record struct CachedHeader(string Value, DateTimeOffset ExpiresAt);
}
