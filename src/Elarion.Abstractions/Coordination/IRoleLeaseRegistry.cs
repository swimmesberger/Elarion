namespace Elarion.Abstractions.Coordination;

/// <summary>
/// The process-local catalog of configured role leases. Consumers use it to answer which coarse
/// roles this process currently owns without knowing the lease provider or performing I/O.
/// </summary>
public interface IRoleLeaseRegistry {
    /// <summary>All role leases configured in this process.</summary>
    IReadOnlyCollection<IRoleLease> Leases { get; }
}
