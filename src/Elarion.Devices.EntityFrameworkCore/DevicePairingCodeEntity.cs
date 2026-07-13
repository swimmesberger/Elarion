namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// The persisted row backing <see cref="IPairingCodeStore"/>: one pending pairing code, stored as
/// the SHA-256 hex of the normalized code — the plain code never reaches the database. Claiming
/// deletes the row (delete = single-use); expired rows wait for
/// <see cref="IPairingCodeStore.DeleteExpiredAsync"/>.
/// </summary>
public sealed class DevicePairingCodeEntity {
    /// <summary>SHA-256 hex of the normalized code (the primary key).</summary>
    public required string CodeHash { get; init; }

    /// <summary>The device id pre-assigned at issue time.</summary>
    public required string DeviceId { get; init; }

    /// <summary>When the code stops being redeemable.</summary>
    public DateTimeOffset ExpiresOnUtc { get; set; }

    /// <summary>When the code was issued.</summary>
    public DateTimeOffset CreatedOnUtc { get; set; }
}
