using Elarion.Coordination.PostgreSql;

namespace Elarion.Actors.PostgreSql;

/// <summary>Configures the opt-in virtual-shard actor placement recipe.</summary>
public sealed class PostgreSqlActorShardingOptions {
    /// <summary>
    /// Number of fixed virtual shards. It is independent of the number of application processes;
    /// the default is deliberately stable so adding processes does not require reconfiguration.
    /// </summary>
    public int VirtualShardCount { get; set; } = 16;

    /// <summary>
    /// Prefix for the generated role names. Shards are registered as
    /// <c>{RolePrefix}:shard-0</c>, <c>{RolePrefix}:shard-1</c>, and so on.
    /// </summary>
    public string RolePrefix { get; set; } = "actors";

    /// <summary>
    /// Optional customization applied to every generated role lease. The extension overwrites
    /// <see cref="RoleLeaseOptions.RoleName"/> after the callback so shard identity stays stable.
    /// </summary>
    public Action<RoleLeaseOptions>? ConfigureLease { get; set; }
}
