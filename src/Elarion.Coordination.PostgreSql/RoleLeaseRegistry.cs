using System.Collections.Concurrent;
using Elarion.Abstractions.Coordination;

namespace Elarion.Coordination.PostgreSql;

internal sealed class RoleLeaseRegistry : IRoleLeaseRegistry {
    private readonly ConcurrentDictionary<string, IRoleLease> _leases = new(StringComparer.Ordinal);

    public IReadOnlyCollection<IRoleLease> Leases => _leases.Values.ToArray();

    public void Add(IRoleLease lease) => _leases.TryAdd(lease.Role, lease);
}
