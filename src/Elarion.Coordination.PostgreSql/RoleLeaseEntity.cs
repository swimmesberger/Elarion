namespace Elarion.Coordination.PostgreSql;

/// <summary>
/// The persisted lease row backing <see cref="Elarion.Abstractions.Coordination.IRoleLease"/>
/// (ADR-0049): one row per role, naming the instance that currently holds it and when the hold
/// expires. The row is the whole membership protocol — acquisition is a conditional upsert,
/// failover is expiry.
/// </summary>
public sealed class RoleLeaseEntity {
    /// <summary>The role name (the primary key); one row per role.</summary>
    public required string Role { get; init; }

    /// <summary>The instance currently holding the lease.</summary>
    public string Owner { get; set; } = "";

    /// <summary>When the current hold expires; another instance may take over past this point.</summary>
    public DateTimeOffset ExpiresOnUtc { get; set; }
}
