using System.Security.Claims;
using System.Security.Cryptography;

namespace Elarion.Devices;

/// <summary>
/// Connect-time device authentication: the server sends a fresh nonce, the device answers
/// <c>HMAC-SHA256(key, nonce)</c>, and <see cref="VerifyAsync"/> checks it in constant time and
/// returns the device principal. Shaped to be called from a connection-handshake authenticator
/// (<c>WebSocketConnectionHandler</c>/<c>TcpConnectionHandler</c>, ADR-0053): verify, then build
/// the ticket with <c>Principal</c> = the result and <c>PrincipalId</c> = the device id.
/// </summary>
/// <remarks>
/// The nonce must be minted per connection attempt (<see cref="CreateNonce"/>) and held only for
/// that handshake — never reused, never persisted — so a captured response can't be replayed; the
/// adapter's handshake timeout bounds its lifetime. Unknown device ids pay the same HMAC cost as
/// known ones, so verification timing does not leak which ids exist.
/// </remarks>
public sealed class HmacChallengeVerifier(IDeviceKeyStore deviceKeys) {
    /// <summary>The HMAC-SHA256 response length in bytes.</summary>
    public const int ResponseSizeBytes = 32;

    // Stands in for the key of an unknown device so that path still computes and compares a MAC.
    private static readonly byte[] DecoyKey = RandomNumberGenerator.GetBytes(32);

    /// <summary>Mints a fresh challenge nonce (CSPRNG; default 32 bytes).</summary>
    /// <param name="sizeBytes">The nonce size in bytes.</param>
    public static byte[] CreateNonce(int sizeBytes = 32) {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizeBytes, 16);
        return RandomNumberGenerator.GetBytes(sizeBytes);
    }

    /// <summary>
    /// Computes the expected response for a key and nonce — the device-side half, exposed for
    /// simulators and tests.
    /// </summary>
    /// <param name="key">The device key.</param>
    /// <param name="nonce">The challenge nonce.</param>
    public static byte[] ComputeResponse(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) {
        return HMACSHA256.HashData(key, nonce);
    }

    /// <summary>
    /// Verifies a challenge response; returns the device principal, or <see langword="null"/> when
    /// the device is unknown or the response does not match (indistinguishable outcomes).
    /// </summary>
    /// <param name="deviceId">The device id the client claims.</param>
    /// <param name="nonce">The nonce this handshake issued.</param>
    /// <param name="response">The client's MAC over the nonce.</param>
    /// <param name="cancellationToken">Cancels the key lookup.</param>
    public async ValueTask<ClaimsPrincipal?> VerifyAsync(
        string deviceId,
        ReadOnlyMemory<byte> nonce,
        ReadOnlyMemory<byte> response,
        CancellationToken cancellationToken = default) {
        if (string.IsNullOrWhiteSpace(deviceId) || nonce.IsEmpty) return null;

        var key = await deviceKeys.GetKeyAsync(deviceId, cancellationToken).ConfigureAwait(false);
        var material = key.HasValue ? key.Value.Span : DecoyKey;
        var expected = HMACSHA256.HashData(material, nonce.Span);
        var matches = CryptographicOperations.FixedTimeEquals(expected, response.Span);
        return matches && key.HasValue ? DevicePrincipal.Create(deviceId) : null;
    }
}
