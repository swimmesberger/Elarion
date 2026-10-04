using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Elarion.Settings.DataProtection;

/// <summary>
/// <see cref="ISettingValueProtector"/> over ASP.NET Core Data Protection. Each setting gets its own protector
/// derived from the setting's purpose, so a payload only decrypts for the setting it was written for. Payloads
/// are URL-safe base64 of the Data Protection ciphertext, which already embeds the key id.
/// </summary>
/// <remarks>
/// Data Protection rotates keys automatically; a payload under a retired key still decrypts and is reported as
/// <see cref="SettingUnprotectResult.RequiresReprotection"/> so the re-protection step rewrites it. Persist the
/// key ring (for example <c>AddDataProtection().PersistKeysToDbContext&lt;T&gt;()</c>) and share it between nodes — a
/// lost key ring makes every protected setting unrecoverable.
/// </remarks>
public sealed class DataProtectionSettingValueProtector(IDataProtectionProvider provider) : ISettingValueProtector {
    /// <summary>The root purpose under which every setting purpose is nested; versioned so the scheme can change.</summary>
    public const string RootPurpose = "Elarion.Settings.v1";

    /// <summary>The scheme identifier persisted as the entry's protection metadata.</summary>
    public const string SchemeId = "aspnet-dp.v1";

    private readonly IDataProtector _root = provider.CreateProtector(RootPurpose);

    /// <inheritdoc />
    public string Scheme => SchemeId;

    /// <inheritdoc />
    public string Protect(string purpose, string plaintext) {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(plaintext);

        var protectedBytes = _root.CreateProtector(purpose).Protect(Encoding.UTF8.GetBytes(plaintext));
        return Base64Url.EncodeToString(protectedBytes);
    }

    /// <inheritdoc />
    public SettingUnprotectResult Unprotect(string purpose, string payload) {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(payload);

        try {
            var protectedBytes = Base64Url.DecodeFromChars(payload);
            var protector = _root.CreateProtector(purpose);

            // IPersistedDataProtector reports a retired or revoked key; revocation is tolerated on read so the
            // value stays readable until it is re-protected.
            if (protector is IPersistedDataProtector persisted) {
                var plain = persisted.DangerousUnprotect(protectedBytes, true, out var requiresMigration,
                    out var wasRevoked);
                return new SettingUnprotectResult(Encoding.UTF8.GetString(plain), requiresMigration || wasRevoked);
            }

            return new SettingUnprotectResult(Encoding.UTF8.GetString(protector.Unprotect(protectedBytes)), false);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) {
            throw new SettingProtectionException("A secret setting payload could not be unprotected.", ex);
        }
    }
}
