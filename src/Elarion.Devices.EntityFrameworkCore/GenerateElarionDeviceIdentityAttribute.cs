namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// Opts a <c>[GenerateDbSets]</c> context into the device identity tables: the bundled generator
/// adds the <c>DbSet&lt;DeviceKeyEntity&gt;</c>/<c>DbSet&lt;DevicePairingCodeEntity&gt;</c> and
/// applies <c>UseElarionDeviceIdentity</c> through the EF generator's per-feature
/// model-configuration seam.
/// </summary>
/// <example>
/// <code>
/// [GenerateDbSets]
/// [GenerateElarionDeviceIdentity]
/// public partial class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options) : DbContext(options);
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GenerateElarionDeviceIdentityAttribute : Attribute {
    /// <summary>Whether table/column names default to snake_case (the Elarion default).</summary>
    public bool SnakeCase { get; set; } = true;

    /// <summary>Overrides the device key table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? KeyTableName { get; set; }

    /// <summary>Overrides the pairing code table name; defaults per <see cref="SnakeCase"/>.</summary>
    public string? PairingCodeTableName { get; set; }

    /// <summary>Optional schema.</summary>
    public string? Schema { get; set; }
}
