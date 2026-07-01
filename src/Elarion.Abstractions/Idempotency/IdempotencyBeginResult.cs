namespace Elarion.Abstractions.Idempotency;

/// <summary>The outcome of claiming an idempotency key via <see cref="IIdempotencyStore.TryBeginAsync"/>.</summary>
public enum IdempotencyBeginStatus {
    /// <summary>The claim succeeded; the caller owns the key and should run the handler.</summary>
    Began,

    /// <summary>The key already completed; the caller should replay the stored payload (no handler run).</summary>
    Replay,

    /// <summary>The key was previously used with a different request body; the caller should reject (422).</summary>
    FingerprintMismatch,

    /// <summary>Another request with the same key is still in flight; the caller should return 409.</summary>
    InProgress,
}

/// <summary>
/// The result of <see cref="IIdempotencyStore.TryBeginAsync"/> — a status plus, for
/// <see cref="IdempotencyBeginStatus.Replay"/>, the stored serialized outcome to replay. A result struct rather
/// than exceptions, mirroring <c>SettingWriteResult</c>.
/// </summary>
public readonly record struct IdempotencyBeginResult {
    private IdempotencyBeginResult(IdempotencyBeginStatus status, string? payload, bool isFailurePayload) {
        Status = status;
        Payload = payload;
        IsFailurePayload = isFailurePayload;
    }

    /// <summary>The claim outcome.</summary>
    public IdempotencyBeginStatus Status { get; }

    /// <summary>The stored serialized outcome to replay, set only when <see cref="Status"/> is <see cref="IdempotencyBeginStatus.Replay"/>.</summary>
    public string? Payload { get; }

    /// <summary>Whether the replayed <see cref="Payload"/> is a stored definitive-failure outcome rather than a success.</summary>
    public bool IsFailurePayload { get; }

    /// <summary>The claim succeeded; run the handler.</summary>
    public static IdempotencyBeginResult Began() => new(IdempotencyBeginStatus.Began, null, false);

    /// <summary>Replay the stored outcome.</summary>
    public static IdempotencyBeginResult Replay(string payload, bool isFailurePayload = false) =>
        new(IdempotencyBeginStatus.Replay, payload, isFailurePayload);

    /// <summary>The key was reused with a different request body.</summary>
    public static IdempotencyBeginResult FingerprintMismatch() =>
        new(IdempotencyBeginStatus.FingerprintMismatch, null, false);

    /// <summary>Another request with the same key is still in flight.</summary>
    public static IdempotencyBeginResult InProgress() => new(IdempotencyBeginStatus.InProgress, null, false);
}
