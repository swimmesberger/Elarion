namespace Elarion.Devices;

/// <summary>Invariants of the device id namespace, shared by the pairing service and the durable stores.</summary>
public static class DeviceIds {
    /// <summary>
    /// The maximum device id length (128) — the width of the durable stores' <c>device_id</c> columns.
    /// <see cref="IDevicePairingService.IssueAsync"/> enforces it at issue time so an oversized
    /// caller-supplied id fails there, not later inside the store.
    /// </summary>
    public const int MaxLength = 128;
}
