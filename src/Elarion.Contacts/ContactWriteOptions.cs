namespace Elarion.Contacts;

/// <summary>The options of a <see cref="ContactWriter"/>.</summary>
public sealed record ContactWriteOptions {
    /// <summary>The defaults: vCard 3.0.</summary>
    public static ContactWriteOptions Default { get; } = new();

    /// <summary>
    /// The vCard version written (FR-052): 3.0 by default, which Apple Contacts, Google Contacts, Android and Outlook
    /// import; 4.0 on request.
    /// </summary>
    public ContactFormatVersion Version { get; init; } = ContactFormatVersion.V3_0;
}
