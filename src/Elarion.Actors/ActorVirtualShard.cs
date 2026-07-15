namespace Elarion.Actors;

/// <summary>
/// Stable virtual-shard hashing shared by the runtime and shard-aware ingress code. This is FNV-1a
/// over the logical actor name, a separator, and the canonical key text; it intentionally does not
/// use <see cref="string.GetHashCode()"/>, whose result is process-randomized.
/// </summary>
public static class ActorVirtualShard {
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>Returns a deterministic shard index in <c>[0, virtualShardCount)</c>.</summary>
    public static int GetShardIndex(string actorName, string key, int virtualShardCount) {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorName);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(virtualShardCount);

        var hash = OffsetBasis;
        foreach (var character in actorName) {
            hash ^= character;
            hash *= Prime;
        }

        hash ^= (uint)actorName.Length;
        hash *= Prime;

        // Keep the two identity components distinct ("ab" + "c" != "a" + "bc"). The length
        // mix above also prevents a separator character in user-controlled key text from becoming
        // an accidental boundary.
        hash ^= 0xff;
        hash *= Prime;
        foreach (var character in key) {
            hash ^= character;
            hash *= Prime;
        }
        hash ^= (uint)key.Length;
        hash *= Prime;

        return (int)(hash % (uint)virtualShardCount);
    }
}
