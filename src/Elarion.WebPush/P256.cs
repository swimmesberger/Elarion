using System.Security.Cryptography;

namespace Elarion.WebPush;

/// <summary>The uncompressed P-256 point encoding (SEC 1 §2.3.3) Web Push uses for every public key.</summary>
internal static class P256 {
    public const int ScalarLength = 32;
    public const int PointLength = 1 + 2 * ScalarLength;
    private const byte UncompressedPrefix = 0x04;

    public static byte[] EncodePoint(ECPoint point) {
        var encoded = new byte[PointLength];
        encoded[0] = UncompressedPrefix;
        point.X.AsSpan().CopyTo(encoded.AsSpan(1, ScalarLength));
        point.Y.AsSpan().CopyTo(encoded.AsSpan(1 + ScalarLength, ScalarLength));
        return encoded;
    }

    public static ECPoint DecodePoint(ReadOnlySpan<byte> encoded) {
        if (!TryDecodePoint(encoded, out var point))
            throw new InvalidOperationException(
                $"A P-256 public key must be a {PointLength}-byte uncompressed point starting with 0x04.");
        return point;
    }

    /// <summary>
    /// Imports an encoded public key for ECDH, or returns <see langword="null"/> when it is malformed or not on
    /// the curve — the check that defends the key agreement against invalid-curve inputs.
    /// </summary>
    public static ECDiffieHellman? TryImportPublicKey(ReadOnlySpan<byte> encoded) {
        if (!TryDecodePoint(encoded, out var point)) return null;
        try {
            return ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = point });
        }
        // An off-curve point surfaces as CryptographicException on OpenSSL but PlatformNotSupportedException on
        // Windows CNG.
        catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException) {
            return null;
        }
    }

    /// <summary>Checks only the encoding; whether the point is on the curve is checked when a key is imported.</summary>
    public static bool TryDecodePoint(ReadOnlySpan<byte> encoded, out ECPoint point) {
        if (encoded.Length != PointLength || encoded[0] != UncompressedPrefix) {
            point = default;
            return false;
        }

        point = new ECPoint {
            X = encoded.Slice(1, ScalarLength).ToArray(),
            Y = encoded.Slice(1 + ScalarLength, ScalarLength).ToArray()
        };
        return true;
    }
}
