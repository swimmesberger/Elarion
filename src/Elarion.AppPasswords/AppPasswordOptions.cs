namespace Elarion.AppPasswords;

/// <summary>
/// The app-password settings, bound by <c>AddElarionAppPasswords(configuration)</c> from the <see cref="SectionName"/>
/// section, for example <c>AppPasswords:MaxPerUser</c>.
/// </summary>
public sealed class AppPasswordOptions {
    /// <summary>The conventional configuration section: <c>AppPasswords</c>.</summary>
    public const string SectionName = "AppPasswords";

    /// <summary>The longest app-password name <see cref="IAppPasswords.CreateAsync"/> accepts: 200 characters.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// The longest user id <see cref="IAppPasswords.CreateAsync"/> accepts: 256 characters, the length ASP.NET Core
    /// Identity gives its keys, and the length of the stored column.
    /// </summary>
    public const int MaxUserIdLength = 256;

    /// <summary>The longest <see cref="SecretPrefix"/>: 32 characters.</summary>
    public const int MaxSecretPrefixLength = 32;

    /// <summary>
    /// The text every new secret starts with, so a leaked secret is recognisable to a secret scanner: up to 32 ASCII
    /// letters, digits, <c>-</c> or <c>_</c>. Default <c>elap_</c>. Only new secrets are affected: the stored hash covers
    /// the whole secret, so secrets issued under another prefix keep verifying.
    /// </summary>
    public string SecretPrefix { get; set; } = "elap_";

    /// <summary>How many app passwords (of any scope) one user may hold; creating one more fails. Default 10.</summary>
    public int MaxPerUser { get; set; } = 10;

    /// <summary>
    /// The shortest time between two writes of an app password's last use; a verification within it writes nothing.
    /// Default 15 minutes; <see cref="TimeSpan.Zero"/> writes on every use.
    /// </summary>
    public TimeSpan LastUseInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The problems of the current values; empty when they are valid.</summary>
    internal IEnumerable<string> Validate() {
        if (SecretPrefix is null)
            yield return "AppPasswords:SecretPrefix must not be null.";
        else if (SecretPrefix.Length > MaxSecretPrefixLength || !AppPasswordSecrets.IsUrlSafe(SecretPrefix))
            yield return
                $"AppPasswords:SecretPrefix must be at most {MaxSecretPrefixLength} ASCII letters, digits, '-' or '_'.";
        if (MaxPerUser < 1)
            yield return "AppPasswords:MaxPerUser must be at least 1.";
        if (LastUseInterval < TimeSpan.Zero)
            yield return "AppPasswords:LastUseInterval must not be negative.";
    }
}
