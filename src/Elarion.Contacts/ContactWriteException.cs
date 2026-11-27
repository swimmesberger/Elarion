namespace Elarion.Contacts;

/// <summary>
/// Thrown when a contact cannot be written as a vCard (FR-061): an additional property whose name, group or parameter
/// name is not a valid vCard name, or whose value holds a line break, or a photo with neither bytes nor an address. The
/// message and the properties name the contact and the property.
/// </summary>
/// <remarks>
/// The writer checks a contact before it writes any of it, so nothing of the failed contact reaches the destination
/// and the writer takes further contacts.
/// </remarks>
public sealed class ContactWriteException : Exception {
    /// <summary>Creates the exception with a message.</summary>
    public ContactWriteException(string message) : base(message) {
    }

    /// <summary>Creates the exception with a message and its cause.</summary>
    public ContactWriteException(string message, Exception innerException) : base(message, innerException) {
    }

    /// <summary>Creates the exception for a contact and a property.</summary>
    public ContactWriteException(string message, int? itemIndex, string? property) : base(message) {
        ItemIndex = itemIndex;
        Property = property;
    }

    /// <summary>The 1-based index of the contact among those the writer was given, or <see langword="null"/>.</summary>
    public int? ItemIndex { get; }

    /// <summary>The property that cannot be written, or <see langword="null"/>.</summary>
    public string? Property { get; }
}
