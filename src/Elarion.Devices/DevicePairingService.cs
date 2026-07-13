using System.Security.Cryptography;

namespace Elarion.Devices;

/// <summary>
/// Default <see cref="IDevicePairingService"/> over the store seams. Issue pre-assigns the device
/// id (v7 — an identifier, not a capability); the <b>code</b> is the capability and is CSPRNG-drawn,
/// stored hashed, and consumed atomically on redeem.
/// </summary>
public sealed class DevicePairingService(
    IPairingCodeStore pairingCodes,
    IDeviceKeyStore deviceKeys,
    DeviceProvisioningOptions provisioningOptions,
    TimeProvider timeProvider) : IDevicePairingService {
    // A same-hash collision needs an identical live code — vanishingly rare at any sane alphabet
    // and length, so a handful of retries is a formality, not a tuning knob.
    private const int IssueAttempts = 5;

    /// <inheritdoc />
    public async ValueTask<PairingCode> IssueAsync(
        PairingCodeIssueOptions? options = null,
        CancellationToken cancellationToken = default) {
        // A caller-supplied id is validated here, at issue, so it fails where the caller can see it —
        // not later at a store's column bound (or worse, only on the durable tier).
        if (options?.DeviceId is { } suppliedId) {
            if (string.IsNullOrWhiteSpace(suppliedId)) {
                throw new ArgumentException("DeviceId must not be empty or whitespace.", nameof(options));
            }

            if (suppliedId.Length > DeviceIds.MaxLength) {
                throw new ArgumentException(
                    $"DeviceId must be at most {DeviceIds.MaxLength} characters (was {suppliedId.Length}) — "
                    + "the bound of the durable device identity stores.",
                    nameof(options));
            }
        }

        var deviceId = options?.DeviceId ?? Guid.CreateVersion7().ToString();
        var expiresAt = timeProvider.GetUtcNow() + (options?.TimeToLive ?? provisioningOptions.CodeTimeToLive);

        for (var attempt = 0; attempt < IssueAttempts; attempt++) {
            var code = PairingCodes.Generate(provisioningOptions.CodeAlphabet, provisioningOptions.CodeLength);
            var created = await pairingCodes.TryCreateAsync(
                    new PairingCodeEntry {
                        // Normalized before hashing so issue and redeem always hash the same
                        // string (options validation additionally pins the alphabet to
                        // normalization-stable characters — this is the belt to that brace).
                        CodeHash = PairingCodes.Hash(PairingCodes.Normalize(code)),
                        DeviceId = deviceId,
                        ExpiresAt = expiresAt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (created) {
                return new PairingCode { Code = code, DeviceId = deviceId, ExpiresAt = expiresAt };
            }
        }

        throw new InvalidOperationException(
            $"Could not issue a unique pairing code after {IssueAttempts} attempts; "
            + "the code space is exhausted or the store is misbehaving.");
    }

    /// <inheritdoc />
    public async ValueTask<DeviceCredentials?> RedeemAsync(string code, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(code);
        var normalized = PairingCodes.Normalize(code);
        if (normalized.Length == 0) {
            return null;
        }

        var entry = await pairingCodes.ClaimAsync(PairingCodes.Hash(normalized), timeProvider.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);
        if (entry is null) {
            return null;
        }

        // Claim-first: a failure past this point burns the code (the human issues a new one) rather
        // than ever leaving a redeemable code alongside minted key material. The put replaces any
        // existing key — a code issued for a provisioned id is the re-key authorization.
        var key = RandomNumberGenerator.GetBytes(provisioningOptions.KeySizeBytes);
        await deviceKeys.PutAsync(entry.DeviceId, key, cancellationToken).ConfigureAwait(false);
        return new DeviceCredentials { DeviceId = entry.DeviceId, Key = key };
    }
}
