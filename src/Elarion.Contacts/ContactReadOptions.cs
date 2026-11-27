using System.Text;
using Elarion.ContentLines;

namespace Elarion.Contacts;

/// <summary>
/// The limits and the fallback encoding of a read (ADR-0110, decisions 3 and 9). <see cref="ContactOptions.ReadDefaults"/>
/// holds the application's defaults; a read that passes its own options uses those instead, so derive them from the
/// defaults with <c>with</c> to change one setting.
/// </summary>
/// <remarks>
/// The defaults are sized for the 1–10 node tier (ADR-0025). A per-contact limit (<see cref="MaxPropertiesPerContact"/>,
/// <see cref="MaxLineBytes"/>, <see cref="MaxPhotoBytes"/>) costs at most that contact or property and the read
/// continues; a file limit (<see cref="MaxBytes"/>, <see cref="MaxContacts"/>) stops the read with one error. The
/// properties are settable because the configuration binder fills <see cref="ContactOptions.ReadDefaults"/> in place;
/// do not change an instance a read may be using.
/// </remarks>
public sealed record ContactReadOptions {
    /// <summary>The default of <see cref="MaxBytes"/>: 32 MiB.</summary>
    public const long DefaultMaxBytes = 32L * 1024 * 1024;

    /// <summary>The default of <see cref="MaxContacts"/>: 10,000.</summary>
    public const int DefaultMaxContacts = 10_000;

    /// <summary>The default of <see cref="MaxPropertiesPerContact"/>: 1,000.</summary>
    public const int DefaultMaxPropertiesPerContact = 1_000;

    /// <summary>The default of <see cref="MaxLineBytes"/>: 1 MiB.</summary>
    public const int DefaultMaxLineBytes = 1024 * 1024;

    /// <summary>The default of <see cref="MaxPhotoBytes"/>: 1 MiB.</summary>
    public const int DefaultMaxPhotoBytes = 1024 * 1024;

    /// <summary>The default of <see cref="FallbackEncoding"/>: <c>windows-1252</c>.</summary>
    public const string DefaultFallbackEncoding = "windows-1252";

    /// <summary>
    /// The most bytes read from the input (FR-046). A larger input is refused with one
    /// <see cref="ContactErrorCodes.TooLarge"/> error, and the reader never holds more than this many bytes of it.
    /// </summary>
    public long MaxBytes { get; set; } = DefaultMaxBytes;

    /// <summary>
    /// The most vCards read (FR-047). Reading stops with <see cref="ContactErrorCodes.TooManyContacts"/> and keeps the
    /// entries before it.
    /// </summary>
    public int MaxContacts { get; set; } = DefaultMaxContacts;

    /// <summary>
    /// The most lines of one vCard between its <c>BEGIN</c> and its <c>END</c> (FR-048). A larger card has no contact
    /// and a <see cref="ContactErrorCodes.TooManyProperties"/> error; the read continues with the next card.
    /// </summary>
    public int MaxPropertiesPerContact { get; set; } = DefaultMaxPropertiesPerContact;

    /// <summary>
    /// The most bytes of one unfolded line other than a photo (FR-049). A longer line is dropped with a
    /// <see cref="ContactErrorCodes.LineTooLong"/> error.
    /// </summary>
    public int MaxLineBytes { get; set; } = DefaultMaxLineBytes;

    /// <summary>
    /// The most decoded bytes of one photo (FR-050). A larger photo is dropped with a
    /// <see cref="ContactErrorCodes.PhotoTooLarge"/> error, without decoding more than the limit.
    /// </summary>
    public int MaxPhotoBytes { get; set; } = DefaultMaxPhotoBytes;

    /// <summary>
    /// The encoding of a value that names no <c>CHARSET</c> and is not valid UTF-8 (FR-013): a name the runtime or its
    /// code-page provider knows, such as <c>windows-1250</c> or <c>iso-8859-2</c>. An unknown name is a misconfiguration
    /// and throws an <see cref="InvalidOperationException"/>.
    /// </summary>
    public string FallbackEncoding { get; set; } = DefaultFallbackEncoding;

    internal Encoding Validate() {
        if (MaxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxBytes), MaxBytes, "The byte limit must be positive.");
        if (MaxContacts <= 0) throw new ArgumentOutOfRangeException(nameof(MaxContacts), MaxContacts, "The contact limit must be positive.");
        if (MaxPropertiesPerContact <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPropertiesPerContact), MaxPropertiesPerContact, "The property limit must be positive.");
        if (MaxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxLineBytes), MaxLineBytes, "The line limit must be positive.");
        if (MaxPhotoBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxPhotoBytes), MaxPhotoBytes, "The photo limit must be positive.");
        if (string.IsNullOrWhiteSpace(FallbackEncoding))
            throw new InvalidOperationException("The fallback encoding of the contact read options is empty.");

        return CodePages.Get(FallbackEncoding);
    }
}
