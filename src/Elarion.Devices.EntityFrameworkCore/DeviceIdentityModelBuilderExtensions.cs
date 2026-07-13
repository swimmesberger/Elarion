using Microsoft.EntityFrameworkCore;

namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// Maps the <see cref="DeviceKeyEntity"/> and <see cref="DevicePairingCodeEntity"/> onto a model.
/// Normally applied through the <see cref="GenerateElarionDeviceIdentityAttribute"/> seam; call it
/// directly from <c>OnModelCreating</c> when the context is hand-written.
/// </summary>
public static class DeviceIdentityModelBuilderExtensions {
    /// <summary>Adds the device key and pairing code tables to the model.</summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="keyTableName">Overrides the device key table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="pairingCodeTableName">Overrides the pairing code table name; defaults per <paramref name="snakeCase"/>.</param>
    /// <param name="schema">Optional schema.</param>
    /// <param name="snakeCase">Whether table/column names default to snake_case (the Elarion default).</param>
    public static ModelBuilder UseElarionDeviceIdentity(
        this ModelBuilder modelBuilder,
        string? keyTableName = null,
        string? pairingCodeTableName = null,
        string? schema = null,
        bool snakeCase = true) {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var keyTable = keyTableName ?? (snakeCase ? "elarion_device_keys" : "ElarionDeviceKeys");
        var codeTable = pairingCodeTableName ?? (snakeCase ? "elarion_device_pairing_codes" : "ElarionDevicePairingCodes");
        ArgumentException.ThrowIfNullOrWhiteSpace(keyTable);
        ArgumentException.ThrowIfNullOrWhiteSpace(codeTable);

        modelBuilder.Entity<DeviceKeyEntity>(builder => {
            builder.ToTable(keyTable, schema);
            builder.HasKey(entity => entity.DeviceId);
            builder.Property(entity => entity.DeviceId)
                .HasColumnName(snakeCase ? "device_id" : "DeviceId")
                .HasMaxLength(DeviceIds.MaxLength);
            builder.Property(entity => entity.Key)
                .HasColumnName(snakeCase ? "key" : "Key");
            builder.Property(entity => entity.CreatedOnUtc)
                .HasColumnName(snakeCase ? "created_on_utc" : "CreatedOnUtc");
        });

        modelBuilder.Entity<DevicePairingCodeEntity>(builder => {
            builder.ToTable(codeTable, schema);
            builder.HasKey(entity => entity.CodeHash);
            builder.Property(entity => entity.CodeHash)
                .HasColumnName(snakeCase ? "code_hash" : "CodeHash")
                .HasMaxLength(64);
            builder.Property(entity => entity.DeviceId)
                .HasColumnName(snakeCase ? "device_id" : "DeviceId")
                .HasMaxLength(DeviceIds.MaxLength);
            builder.Property(entity => entity.ExpiresOnUtc)
                .HasColumnName(snakeCase ? "expires_on_utc" : "ExpiresOnUtc");
            builder.Property(entity => entity.CreatedOnUtc)
                .HasColumnName(snakeCase ? "created_on_utc" : "CreatedOnUtc");
            // The expired-code sweep's scan column, mirroring the staged-upload/idempotency stores.
            builder.HasIndex(entity => entity.ExpiresOnUtc);
        });
        return modelBuilder;
    }
}
