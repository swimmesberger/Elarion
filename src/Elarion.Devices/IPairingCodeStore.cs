namespace Elarion.Devices;

/// <summary>
/// Storage seam for pending pairing codes. Codes are stored <b>hashed</b> (SHA-256 of the
/// normalized code) so a leaked table never yields redeemable codes; the plain code exists only in
/// the issue response handed to the human. Claiming is atomic and single-use — exactly one
/// concurrent redeem wins.
/// </summary>
public interface IPairingCodeStore {
    /// <summary>
    /// Atomically stores a pending code and supersedes every earlier pending code for the same device.
    /// Returns <see langword="false"/> when an entry with the same code hash already exists — live or
    /// expired-but-unswept — and leaves all existing entries untouched, so the issuer retries with a
    /// fresh code without accidentally revoking a valid capability after a hash collision.
    /// </summary>
    /// <param name="entry">The pending code entry.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    ValueTask<bool> TryReplaceAsync(PairingCodeEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Revokes every pending pairing code for <paramref name="deviceId"/>. A concurrent claim
    /// remains single-winner: either it consumes the code first or this call removes it first.</summary>
    /// <returns>How many pending codes were revoked.</returns>
    ValueTask<int> RevokeAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically consumes the entry with <paramref name="codeHash"/> and returns it, or
    /// <see langword="null"/> when no entry exists, it has expired, or another redeem already
    /// claimed it — deliberately indistinguishable outcomes.
    /// </summary>
    /// <param name="codeHash">The hash of the normalized code being redeemed.</param>
    /// <param name="now">The current instant; entries at or past their expiry never claim.</param>
    /// <param name="cancellationToken">Cancels the claim.</param>
    ValueTask<PairingCodeEntry?> ClaimAsync(string codeHash, DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes expired entries (garbage collection); returns how many were removed.</summary>
    /// <param name="now">The current instant.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    ValueTask<int> DeleteExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
}

/// <summary>A pending pairing code as the store persists it — hash only, never the plain code.</summary>
public sealed record PairingCodeEntry {
    /// <summary>SHA-256 hex of the normalized code.</summary>
    public required string CodeHash { get; init; }

    /// <summary>The device id assigned at issue time and returned to the redeeming device.</summary>
    public required string DeviceId { get; init; }

    /// <summary>When the code stops being redeemable.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
