namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// The persisted row backing <see cref="IVapidKeyStore"/>: the application server's VAPID key pair. The
/// table holds one row, keyed by <see cref="Name"/>, so concurrent first inserts collide on the primary key
/// and exactly one wins.
/// </summary>
public sealed class VapidKeyEntity {
    /// <summary>The row name the store uses.</summary>
    public const string DefaultName = "default";

    /// <summary>The key pair's name (<see cref="DefaultName"/>).</summary>
    public required string Name { get; init; }

    /// <summary>The public key (base64url uncompressed P-256 point).</summary>
    public required string PublicKey { get; init; }

    /// <summary>
    /// The private key as persisted: the opaque payload of the registered <c>ISettingValueProtector</c> when
    /// <see cref="Protection"/> is set, or — on a row written before protection existed — the plain base64url
    /// P-256 scalar, which the store re-protects on first read.
    /// </summary>
    public required string PrivateKey { get; init; }

    /// <summary>
    /// The <c>ISettingValueProtector.Scheme</c> that protected <see cref="PrivateKey"/>, or <see langword="null"/>
    /// for a legacy plaintext row (ADR-0078's protection metadata).
    /// </summary>
    public string? Protection { get; init; }

    /// <summary>When the pair was generated.</summary>
    public DateTimeOffset CreatedOnUtc { get; init; }
}
