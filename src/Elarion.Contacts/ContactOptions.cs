namespace Elarion.Contacts;

/// <summary>
/// The application's contact settings, bound by <c>AddElarionContacts(configuration)</c> from the
/// <see cref="SectionName"/> section, for example <c>Contacts:ReadDefaults:MaxContacts</c>.
/// </summary>
public sealed class ContactOptions {
    /// <summary>The conventional configuration section: <c>Contacts</c>.</summary>
    public const string SectionName = "Contacts";

    /// <summary>The read options a read uses when it passes none.</summary>
    public ContactReadOptions ReadDefaults { get; set; } = new();
}
