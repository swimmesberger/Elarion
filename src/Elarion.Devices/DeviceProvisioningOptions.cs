namespace Elarion.Devices;

/// <summary>Process-wide provisioning defaults, configured on <c>AddElarionDeviceIdentity</c>.</summary>
public sealed class DeviceProvisioningOptions {
    /// <summary>
    /// The default code alphabet: digits and uppercase letters minus the visually ambiguous
    /// <c>0 1 I L O U</c> (Crockford-style), so codes survive being read aloud or typed from a label.
    /// </summary>
    public const string DefaultCodeAlphabet = "23456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Characters a generated code draws from. Sampling is unbiased for any alphabet size.</summary>
    public string CodeAlphabet { get; set; } = DefaultCodeAlphabet;

    /// <summary>
    /// Generated code length (default 8 — ~39 bits over the default alphabet, sized for a
    /// short-TTL, single-use code behind a rate-limited endpoint, not for an unthrottled oracle).
    /// </summary>
    public int CodeLength { get; set; } = 8;

    /// <summary>How long an issued code stays redeemable (default 10 minutes).</summary>
    public TimeSpan CodeTimeToLive { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Minted device key size in bytes (default 32 — a full HMAC-SHA256 key).</summary>
    public int KeySizeBytes { get; set; } = 32;

    internal void Validate() {
        if (string.IsNullOrEmpty(CodeAlphabet) || CodeAlphabet.Length < 10) {
            throw new ArgumentException("CodeAlphabet must contain at least 10 characters.", nameof(CodeAlphabet));
        }

        // Duplicates would silently bias generation (GetItems samples the string uniformly,
        // duplicates included), collapsing the real entropy below what the length suggests.
        if (CodeAlphabet.Distinct().Count() != CodeAlphabet.Length) {
            throw new ArgumentException("CodeAlphabet must not contain duplicate characters.", nameof(CodeAlphabet));
        }

        // Redeem normalizes input (uppercase, separators stripped) before hashing, so every
        // alphabet character must survive normalization unchanged — otherwise issued codes could
        // never be redeemed (issue and redeem would hash different strings).
        if (PairingCodes.Normalize(CodeAlphabet) != CodeAlphabet) {
            throw new ArgumentException(
                "CodeAlphabet must be normalization-stable: uppercase characters only, no separators ('-', '.') or whitespace.",
                nameof(CodeAlphabet));
        }

        if (CodeLength < 6) {
            throw new ArgumentException("CodeLength must be at least 6.", nameof(CodeLength));
        }

        if (CodeTimeToLive <= TimeSpan.Zero) {
            throw new ArgumentException("CodeTimeToLive must be positive.", nameof(CodeTimeToLive));
        }

        if (KeySizeBytes < 16) {
            throw new ArgumentException("KeySizeBytes must be at least 16.", nameof(KeySizeBytes));
        }
    }
}
