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

    /// <summary>
    /// Whether lines longer than 75 octets are folded (FR-081): <see langword="true"/> by default, as RFC 2426 and RFC
    /// 6350 ask. <see langword="false"/> writes each property on one line, for a payload that is not a file, such as a
    /// QR code or an NFC tag, whose readers do not unfold.
    /// </summary>
    public bool FoldLines { get; init; } = true;
}
