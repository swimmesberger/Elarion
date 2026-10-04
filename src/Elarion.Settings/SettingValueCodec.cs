namespace Elarion.Settings;

/// <summary>
/// Applies the protector to stored values. Protection state is read from and written to the store entry's
/// metadata (<see cref="SettingEntry.Protection"/>), never inferred from the value itself.
/// </summary>
/// <remarks>
/// The protector purpose binds a payload to the scope kind, the owner, and the key, so a ciphertext copied to
/// another row (another user, another key) does not decrypt. Renaming a secret key therefore requires writing its
/// value again.
/// </remarks>
internal sealed class SettingValueCodec(ISettingValueProtector? protector) {
    public (string? Value, string? Protection) Encode(SettingDefinition definition, SettingsScope scope, string json) {
        if (!definition.IsSecret) return (json, null);

        var current = protector ?? throw MissingProtector(definition.Key);
        return (current.Protect(CreatePurpose(scope, definition.Key), json), current.Scheme);
    }

    public (string Json, bool RequiresReprotection) Decode(SettingsScope scope, string key, SettingEntry entry,
        bool isSecret) {
        if (entry.Value is null) return ("null", false);

        if (entry.Protection is null)
            // Plaintext row: readable as-is, and worth protecting if the definition is (now) secret.
            return (entry.Value, isSecret);

        var current = protector ?? throw MissingProtector(key);
        if (!string.Equals(entry.Protection, current.Scheme, StringComparison.Ordinal))
            throw new SettingProtectionException(
                $"Setting '{key}' is stored with protection scheme '{entry.Protection}', but the registered " +
                $"{nameof(ISettingValueProtector)} handles '{current.Scheme}'.");

        try {
            var result = current.Unprotect(CreatePurpose(scope, key), entry.Value);
            return (result.Plaintext, result.RequiresReprotection);
        }
        catch (SettingProtectionException) {
            throw;
        }
        catch (Exception ex) {
            throw new SettingProtectionException($"Setting '{key}' could not be unprotected.", ex);
        }
    }

    /// <summary>
    /// Builds the unambiguous protector purpose for a setting. Length-prefixing the owner keeps
    /// <c>(owner "a", key "b:c")</c> and <c>(owner "a:b", key "c")</c> distinct.
    /// </summary>
    internal static string CreatePurpose(SettingsScope scope, string key) {
        return $"scope:{scope.Kind.Length}:{scope.Kind};owner:{scope.Owner?.Length ?? -1}:{scope.Owner};key:{key}";
    }

    private static SettingProtectionException MissingProtector(string key) {
        return new SettingProtectionException(
            $"Setting '{key}' needs an {nameof(ISettingValueProtector)} but none is registered. Register one, for " +
            "example AddElarionSettingsDataProtection from Elarion.Settings.DataProtection.");
    }
}
