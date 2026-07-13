namespace Elarion.Devices;

/// <summary>
/// The pairing/provisioning flow (ADR-0054): <see cref="IssueAsync"/> mints a short-lived,
/// single-use, human-typeable claim code bound to a fresh device id; <see cref="RedeemAsync"/> is
/// called by the device (over a rate-limited anonymous endpoint the app owns) to trade the code
/// for its identity and symmetric key.
/// </summary>
public interface IDevicePairingService {
    /// <summary>
    /// Issues a pairing code. The returned <see cref="PairingCode.DeviceId"/> is assigned up front
    /// so the issuer can attach the pending device to its own domain state before the device ever
    /// redeems.
    /// </summary>
    /// <param name="options">Optional per-issue overrides (TTL, caller-supplied device id).</param>
    /// <param name="cancellationToken">Cancels the issue.</param>
    /// <exception cref="ArgumentException">A caller-supplied <see cref="PairingCodeIssueOptions.DeviceId"/>
    /// is blank or longer than <see cref="DeviceIds.MaxLength"/> characters.</exception>
    ValueTask<PairingCode> IssueAsync(PairingCodeIssueOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Redeems a code: atomically consumes it, mints the device key, stores it, and returns the
    /// credentials — or <see langword="null"/> when the code is unknown, expired, or already used
    /// (deliberately indistinguishable). Input is normalized (case, separators, whitespace), so
    /// devices may echo the code exactly as a human typed it. Redeeming a code issued for an
    /// already-provisioned device id <b>replaces</b> its key — issuing that code is the re-key
    /// authorization (device reset, lost key).
    /// </summary>
    /// <param name="code">The pairing code as received from the device.</param>
    /// <param name="cancellationToken">Cancels the redeem.</param>
    ValueTask<DeviceCredentials?> RedeemAsync(string code, CancellationToken cancellationToken = default);
}

/// <summary>An issued pairing code — the only place the plain code ever exists server-side.</summary>
public sealed record PairingCode {
    /// <summary>The plain code to hand to the human (display formatting/grouping is the app's).</summary>
    public required string Code { get; init; }

    /// <summary>The device id the code will provision.</summary>
    public required string DeviceId { get; init; }

    /// <summary>When the code stops being redeemable.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>Per-issue overrides for <see cref="IDevicePairingService.IssueAsync"/>.</summary>
public sealed record PairingCodeIssueOptions {
    /// <summary>Overrides the code lifetime for this issue; defaults to <see cref="DeviceProvisioningOptions.CodeTimeToLive"/>.</summary>
    public TimeSpan? TimeToLive { get; init; }

    /// <summary>
    /// Uses a caller-supplied device id instead of a minted one — for apps with their own device id
    /// scheme, and the re-pairing path: a code issued for an existing device id rotates its key at
    /// redeem. Validated at issue: non-blank, at most <see cref="DeviceIds.MaxLength"/> characters
    /// (the durable stores' column bound).
    /// </summary>
    public string? DeviceId { get; init; }
}

/// <summary>The credentials a successful redeem hands to the device.</summary>
public sealed record DeviceCredentials {
    /// <summary>The device's stable identity (the connection-registry principal id for device links).</summary>
    public required string DeviceId { get; init; }

    /// <summary>The device's symmetric key. Transport encoding (base64/hex) is the endpoint's choice.</summary>
    public required ReadOnlyMemory<byte> Key { get; init; }
}
