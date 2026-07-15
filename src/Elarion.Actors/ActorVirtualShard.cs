using Elarion.Abstractions.Coordination;

namespace Elarion.Actors;

/// <summary>
/// Stable virtual-shard hashing shared by the runtime and shard-aware ingress code. This is FNV-1a
/// over the logical actor name, a separator, and the canonical key text; it intentionally does not
/// use <see cref="string.GetHashCode()"/>, whose result is process-randomized.
/// </summary>
public static class ActorVirtualShard {
    /// <summary>Returns a deterministic shard index in <c>[0, virtualShardCount)</c>.</summary>
    public static int GetShardIndex(string actorName, string key, int virtualShardCount) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(virtualShardCount);

        return RolePartitionHash.GetPartition(actorName, key, virtualShardCount);
    }
}
