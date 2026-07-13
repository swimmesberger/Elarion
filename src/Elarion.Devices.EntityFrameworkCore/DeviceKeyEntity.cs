namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// The persisted row backing <see cref="IDeviceKeyStore"/>: one symmetric key per provisioned
/// device, keyed by device id.
/// </summary>
public sealed class DeviceKeyEntity {
    /// <summary>The device's stable identity.</summary>
    public required string DeviceId { get; init; }

    /// <summary>The raw symmetric key material (CSPRNG-minted at redeem).</summary>
    public required byte[] Key { get; init; }

    /// <summary>When the device was provisioned.</summary>
    public DateTimeOffset CreatedOnUtc { get; set; }
}
