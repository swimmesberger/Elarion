namespace Elarion.Settings;

/// <summary>
/// The protection seam for secret settings: turns a plaintext value into an opaque payload that is safe to
/// persist, and back. The settings subsystem stores the payload as the entry's value and the protector's
/// <see cref="Scheme"/> as the entry's protection metadata (<see cref="SettingEntry.Protection"/>); an
/// implementation only handles the payload. <c>Elarion.Settings.DataProtection</c> is the shipped implementation
/// over ASP.NET Core Data Protection; a KMS- or vault-backed implementation implements the same contract.
/// </summary>
/// <remarks>
/// Implementations must be thread-safe, must bind the payload to <c>purpose</c> (a payload protected for one
/// purpose must not unprotect for another), and must throw <see cref="SettingProtectionException"/> when a
/// payload cannot be unprotected rather than returning garbage. Registering a protector is mandatory as soon as a
/// secret definition is registered: there is no unprotected fallback.
/// </remarks>
public interface ISettingValueProtector {
    /// <summary>
    /// A short stable identifier of the protection scheme and its payload format (for example
    /// <c>"aspnet-dp.v1"</c>), persisted as the entry's protection metadata. Changing it makes existing entries
    /// report <see cref="SettingUnprotectResult.RequiresReprotection"/>.
    /// </summary>
    string Scheme { get; }

    /// <summary>Protects <paramref name="plaintext"/> for <paramref name="purpose"/>.</summary>
    /// <param name="purpose">
    /// A string identifying the setting (scope, owner, and key). The payload must only unprotect with the same
    /// purpose.
    /// </param>
    /// <param name="plaintext">The value to protect.</param>
    /// <returns>An opaque, text-safe payload.</returns>
    string Protect(string purpose, string plaintext);

    /// <summary>Unprotects a payload produced by <see cref="Protect"/> for the same <paramref name="purpose"/>.</summary>
    /// <exception cref="SettingProtectionException">The payload is malformed, tampered with, or its key is gone.</exception>
    SettingUnprotectResult Unprotect(string purpose, string payload);
}

/// <summary>The outcome of unprotecting a payload.</summary>
/// <param name="Plaintext">The recovered value.</param>
/// <param name="RequiresReprotection">
/// Whether the payload was protected with a key that is no longer current (rotated or revoked), so the value
/// should be written back under the current key.
/// </param>
public readonly record struct SettingUnprotectResult(string Plaintext, bool RequiresReprotection);

/// <summary>
/// Thrown when a secret setting cannot be protected or unprotected: no protector is registered, or the stored
/// payload cannot be decrypted. The message never contains a setting value.
/// </summary>
public sealed class SettingProtectionException : InvalidOperationException {
    /// <summary>Creates the exception with a message.</summary>
    public SettingProtectionException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and the underlying cause.</summary>
    public SettingProtectionException(string message, Exception innerException) : base(message, innerException) {
    }
}
