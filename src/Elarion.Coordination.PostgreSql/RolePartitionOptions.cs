namespace Elarion.Coordination.PostgreSql;

/// <summary>Configures a fixed virtual partition over PostgreSQL role leases.</summary>
public sealed class RolePartitionOptions {
    /// <summary>The partition name and role prefix.</summary>
    public required string Name { get; set; }

    /// <summary>The fixed virtual partition count. Defaults to 16.</summary>
    public int PartitionCount { get; set; } = 16;

    /// <summary>Optional customization applied to every generated lease.</summary>
    public Action<RoleLeaseOptions>? ConfigureLease { get; set; }
}
