namespace Elarion.Idempotency.EntityFrameworkCore;

/// <summary>
/// The persisted idempotency record. The composite key <c>(Scope, Owner, Key)</c> is the unique constraint that
/// serializes concurrent duplicates (including across nodes, since every node contends on the same row). A
/// non-user scope stores an empty <see cref="Owner"/> because a relational primary key column cannot be
/// nullable — mirroring the settings <c>Setting</c> entity.
/// </summary>
public sealed class IdempotencyKeyEntity {
    /// <summary>The scope discriminator (<c>"user"</c> or <c>"global"</c>).</summary>
    public required string Scope { get; init; }

    /// <summary>The scope owner (a hashed user id for the user scope), or the empty string for a global key.</summary>
    public required string Owner { get; init; }

    /// <summary>The client-supplied idempotency key value.</summary>
    public required string Key { get; init; }

    /// <summary>A hash of the request body, used to reject reuse of the key with a different request.</summary>
    public string Fingerprint { get; set; } = string.Empty;

    /// <summary>Whether the record has been finalized with a stored outcome (a pending claim is not committed in the single-transaction model).</summary>
    public bool Completed { get; set; }

    /// <summary>Whether the stored <see cref="Payload"/> is a definitive-failure outcome rather than a success.</summary>
    public bool IsFailure { get; set; }

    /// <summary>The serialized outcome to replay, set on completion.</summary>
    public string? Payload { get; set; }

    /// <summary>When the record was first claimed.</summary>
    public DateTimeOffset CreatedOnUtc { get; set; }

    /// <summary>When the record was finalized.</summary>
    public DateTimeOffset? CompletedOnUtc { get; set; }

    /// <summary>When the record stops being replayable and becomes eligible for purge.</summary>
    public DateTimeOffset? ExpiresOnUtc { get; set; }

    /// <summary>The optimistic-concurrency version.</summary>
    public int Version { get; set; }
}
