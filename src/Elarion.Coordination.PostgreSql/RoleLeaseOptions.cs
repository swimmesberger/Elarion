namespace Elarion.Coordination.PostgreSql;

/// <summary>Configures one PostgreSQL role lease (ADR-0049).</summary>
public sealed class RoleLeaseOptions {
    /// <summary>
    /// The role this lease elects a holder for (the DI key of the registered
    /// <c>IRoleLease</c>, and the primary key of its row). Required.
    /// </summary>
    public string RoleName { get; set; } = "";

    /// <summary>
    /// How long one successful acquisition holds the role. Also the failover bound: a crashed
    /// holder's role is taken over once this expires. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the holder renews (and non-holders re-attempt). Defaults to 10 seconds.</summary>
    public TimeSpan RenewInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long before actual lease expiry <c>IRoleLease.IsHeld</c> turns <see langword="false"/>
    /// locally, so this instance stops acting as the holder before another can legitimately take
    /// over. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan HeldSafetyMargin { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// This instance's identity in the lease row (shown in diagnostics and logs). Defaults to
    /// <c>{machine name}:{random suffix}</c> — unique per process, so two processes on one machine
    /// never mistake each other for themselves.
    /// </summary>
    public string InstanceId { get; set; } = $"{Environment.MachineName}:{Guid.CreateVersion7():N}";
}
