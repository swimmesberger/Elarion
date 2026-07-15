namespace Elarion.Abstractions.Coordination;

/// <summary>
/// A fixed set of virtual partitions backed by named role leases. Partition count is independent
/// of process count: one process may own any number of partitions.
/// </summary>
public interface IRolePartition {
    /// <summary>The stable partition name and role-name prefix.</summary>
    string Name { get; }

    /// <summary>The fixed number of virtual partitions.</summary>
    int PartitionCount { get; }

    /// <summary>Resolves an affinity key to its current role-holder view without I/O.</summary>
    RolePartitionTarget Resolve(string affinityKey);

    /// <summary>
    /// Resolves a scoped affinity key without requiring callers to allocate a combined key string.
    /// </summary>
    RolePartitionTarget Resolve(string affinityScope, string affinityKey);
}

/// <summary>The local ownership view for one virtual partition.</summary>
public readonly record struct RolePartitionTarget(
    int Partition,
    string Role,
    bool IsHeld,
    string? CurrentHolder,
    string? CurrentHolderAddress);

/// <summary>Stable partition hashing shared by role-affine ingress and background delivery.</summary>
public static class RolePartitionHash {
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Returns a deterministic partition index in <c>[0, partitionCount)</c>.</summary>
    public static int GetPartition(string affinityKey, int partitionCount) {
        ArgumentNullException.ThrowIfNull(affinityKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        var hash = OffsetBasis;
        foreach (var character in affinityKey) {
            hash ^= character;
            hash *= Prime;
        }

        hash ^= (uint)affinityKey.Length;
        hash *= Prime;
        return (int)(hash % (uint)partitionCount);
    }

    /// <summary>Hashes two unambiguously separated identity components without allocating.</summary>
    public static int GetPartition(string affinityScope, string affinityKey, int partitionCount) {
        ArgumentException.ThrowIfNullOrWhiteSpace(affinityScope);
        ArgumentNullException.ThrowIfNull(affinityKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(partitionCount);

        var hash = OffsetBasis;
        Add(ref hash, affinityScope);
        hash ^= (uint)affinityScope.Length;
        hash *= Prime;
        hash ^= 0xff;
        hash *= Prime;
        Add(ref hash, affinityKey);
        hash ^= (uint)affinityKey.Length;
        hash *= Prime;
        return (int)(hash % (uint)partitionCount);
    }

    /// <summary>
    /// Combines two identity components without allowing their boundary to become ambiguous.
    /// </summary>
    public static string Combine(string scope, string key) {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentNullException.ThrowIfNull(key);
        return $"{scope.Length}:{scope}{key.Length}:{key}";
    }

    private static void Add(ref ulong hash, string value) {
        foreach (var character in value) {
            hash ^= character;
            hash *= Prime;
        }
    }
}
