using Elarion.Abstractions.Coordination;

namespace Elarion.Coordination.PostgreSql;

internal sealed class PostgreSqlRolePartition(string name, IReadOnlyList<IRoleLease> leases) : IRolePartition {
    public string Name { get; } = name;

    public int PartitionCount => leases.Count;

    public RolePartitionTarget Resolve(string affinityKey) {
        var partition = RolePartitionHash.GetPartition(affinityKey, leases.Count);
        return Target(partition);
    }

    public RolePartitionTarget Resolve(string affinityScope, string affinityKey) {
        var partition = RolePartitionHash.GetPartition(affinityScope, affinityKey, leases.Count);
        return Target(partition);
    }

    private RolePartitionTarget Target(int partition) {
        var lease = leases[partition];
        return new RolePartitionTarget(
            partition,
            lease.Role,
            lease.IsHeld,
            lease.CurrentHolder,
            lease.CurrentHolderAddress);
    }
}
