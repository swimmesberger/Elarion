using System.Buffers.Text;
using System.Security.Cryptography;

namespace Elarion.WebPush;

/// <summary>
/// The application server's VAPID key pair (RFC 8292): a P-256 key whose public half browsers bind each
/// subscription to (<c>applicationServerKey</c>). Keep it stable — rotating it invalidates every existing
/// subscription, because the push service rejects messages signed by a different key.
/// </summary>
public sealed record VapidKeys {
    /// <summary>The public key: base64url of the 65-byte uncompressed P-256 point (<c>0x04 ‖ X ‖ Y</c>).</summary>
    public required string PublicKey { get; init; }

    /// <summary>The private key: base64url of the 32-byte P-256 scalar. A secret.</summary>
    public required string PrivateKey { get; init; }

    /// <summary>Generates a fresh key pair from the platform CSPRNG.</summary>
    public static VapidKeys Generate() {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = key.ExportParameters(true);
        return new VapidKeys {
            PublicKey = Base64Url.EncodeToString(P256.EncodePoint(parameters.Q)),
            PrivateKey = Base64Url.EncodeToString(parameters.D)
        };
    }

    /// <summary>Excluded so a logged or exceptioned record never prints the private key.</summary>
    /// <returns>The public key only.</returns>
    public override string ToString() {
        return $"VapidKeys {{ PublicKey = {PublicKey} }}";
    }

    /// <summary>Throws when the pair is malformed or its halves do not belong together.</summary>
    internal void Validate() {
        using var signer = CreateSigner();
        using var verifier = ECDsa.Create(new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = P256.DecodePoint(DecodeOrThrow(PublicKey, nameof(PublicKey)))
        });
        ReadOnlySpan<byte> probe = "elarion-vapid-probe"u8;
        if (!verifier.VerifyData(probe, signer.SignData(probe, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256))
            throw new InvalidOperationException("The VAPID public key does not match the private key.");
    }

    /// <summary>The ES256 signer for VAPID tokens. The caller owns (disposes) it.</summary>
    internal ECDsa CreateSigner() {
        var privateKey = DecodeOrThrow(PrivateKey, nameof(PrivateKey));
        if (privateKey.Length != P256.ScalarLength)
            throw new InvalidOperationException(
                $"The VAPID private key must decode to {P256.ScalarLength} bytes, not {privateKey.Length}.");
        return ECDsa.Create(new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = P256.DecodePoint(DecodeOrThrow(PublicKey, nameof(PublicKey)))
        });
    }

    private static byte[] DecodeOrThrow(string value, string name) {
        try {
            return Base64Url.DecodeFromChars(value);
        }
        catch (FormatException ex) {
            throw new InvalidOperationException($"The VAPID {name} is not valid base64url.", ex);
        }
    }
}
