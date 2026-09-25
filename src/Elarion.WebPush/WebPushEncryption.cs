using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Elarion.WebPush;

/// <summary>
/// RFC 8291 message encryption: the single-record <c>aes128gcm</c> content coding (RFC 8188) keyed by an
/// ephemeral ECDH exchange with the subscription's <c>p256dh</c> key, bound to its <c>auth</c> secret.
/// Built on the platform primitives (ECDH, HKDF, AES-GCM), so it stays AOT-safe with no crypto dependency.
/// </summary>
internal static class WebPushEncryption {
    /// <summary>The largest body a push service must accept (RFC 8030 §7.2).</summary>
    public const int RecordSize = 4096;

    public const int AuthSecretLength = 16;
    private const int SaltLength = 16;
    private const int TagLength = 16;
    private const int HeaderLength = SaltLength + sizeof(uint) + 1 + P256.PointLength;
    private const int CekLength = 16;
    private const int NonceLength = 12;

    // The record delimiter RFC 8188 appends to the last (here: only) record's plaintext.
    private const byte LastRecordDelimiter = 0x02;

    /// <summary>
    /// The largest plaintext that fits one record: 4096 minus the 86-byte header, the 16-byte GCM tag, and
    /// the 1-byte delimiter (RFC 8291 §4).
    /// </summary>
    public const int MaxPlaintextLength = RecordSize - HeaderLength - TagLength - 1;

    private static ReadOnlySpan<byte> KeyInfoPrefix => "WebPush: info\0"u8;
    private static ReadOnlySpan<byte> CekInfo => "Content-Encoding: aes128gcm\0"u8;
    private static ReadOnlySpan<byte> NonceInfo => "Content-Encoding: nonce\0"u8;

    /// <summary>Encrypts <paramref name="plaintext"/> for one subscription with a fresh ephemeral key and salt.</summary>
    /// <exception cref="CryptographicException">The subscription's public key is not a valid P-256 point.</exception>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret) {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Span<byte> salt = stackalloc byte[SaltLength];
        RandomNumberGenerator.Fill(salt);
        return Encrypt(plaintext, userAgentPublicKey, authSecret, ephemeral, salt);
    }

    /// <summary>The deterministic core, with the ephemeral key and salt supplied (the RFC 8291 test vector needs both).</summary>
    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret, ECDiffieHellman applicationServerKey, ReadOnlySpan<byte> salt) {
        if (plaintext.Length > MaxPlaintextLength)
            throw new ArgumentException(
                $"The push payload is {plaintext.Length} bytes; at most {MaxPlaintextLength} fit one message.",
                nameof(plaintext));
        if (authSecret.Length != AuthSecretLength)
            throw new CryptographicException($"The subscription auth secret must be {AuthSecretLength} bytes.");
        if (salt.Length != SaltLength) throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
        using var userAgentKey = P256.TryImportPublicKey(userAgentPublicKey)
                                 ?? throw new CryptographicException(
                                     "The subscription p256dh key is not a valid uncompressed P-256 point.");
        var ecdhSecret = applicationServerKey.DeriveRawSecretAgreement(userAgentKey.PublicKey);
        var applicationServerPublicKey = P256.EncodePoint(applicationServerKey.ExportParameters(false).Q);

        // key_info = "WebPush: info" ‖ 0x00 ‖ ua_public ‖ as_public; IKM = HKDF(auth_secret, ecdh_secret, key_info, 32).
        Span<byte> keyInfo = stackalloc byte[KeyInfoPrefix.Length + 2 * P256.PointLength];
        KeyInfoPrefix.CopyTo(keyInfo);
        userAgentPublicKey.CopyTo(keyInfo[KeyInfoPrefix.Length..]);
        applicationServerPublicKey.CopyTo(keyInfo[(KeyInfoPrefix.Length + P256.PointLength)..]);
        Span<byte> ikm = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, ikm, authSecret, keyInfo);
        CryptographicOperations.ZeroMemory(ecdhSecret);

        // RFC 8188: PRK = HKDF-Extract(salt, IKM); CEK and NONCE are expanded from it.
        Span<byte> prk = stackalloc byte[32];
        HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt, prk);
        Span<byte> cek = stackalloc byte[CekLength];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, cek, CekInfo);
        Span<byte> nonce = stackalloc byte[NonceLength];
        HKDF.Expand(HashAlgorithmName.SHA256, prk, nonce, NonceInfo);

        // Body: salt ‖ rs (uint32 BE) ‖ idlen ‖ keyid (as_public) ‖ ciphertext ‖ tag.
        var body = new byte[HeaderLength + plaintext.Length + 1 + TagLength];
        var span = body.AsSpan();
        salt.CopyTo(span);
        BinaryPrimitives.WriteUInt32BigEndian(span[SaltLength..], RecordSize);
        span[SaltLength + sizeof(uint)] = P256.PointLength;
        applicationServerPublicKey.CopyTo(span[(SaltLength + sizeof(uint) + 1)..]);

        var padded = span.Slice(HeaderLength, plaintext.Length + 1);
        plaintext.CopyTo(padded);
        padded[^1] = LastRecordDelimiter;
        using (var aes = new AesGcm(cek, TagLength))
            // In-place: the ciphertext overwrites the padded plaintext it was written into.
            aes.Encrypt(nonce, padded, padded, span.Slice(HeaderLength + padded.Length, TagLength));

        CryptographicOperations.ZeroMemory(ikm);
        CryptographicOperations.ZeroMemory(prk);
        CryptographicOperations.ZeroMemory(cek);
        return body;
    }
}
