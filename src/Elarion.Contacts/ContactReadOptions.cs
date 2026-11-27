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
/// <see cref="MaxParametersPerProperty"/>, <see cref="MaxLineBytes"/>, <see cref="MaxPhotoBytes"/>) costs at most that contact or property and the read
/// continues; a file limit (<see cref="MaxBytes"/>, <see cref="MaxContacts"/>, <see cref="MaxProperties"/>) stops the
/// read with one error; <see cref="MaxErrors"/> stops listing errors, not the read. The bytes bound the input, the
/// properties, their parameters and the errors bound what the result holds: a result holds 60 to 150 bytes per property
/// line and parameter value and at most about five times the bytes of its values (ADR-0110, amendments of 2026-11-27),
/// so the defaults keep one result under about 200 MB whatever the file holds. The
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

    /// <summary>The default of <see cref="MaxProperties"/>: 1,000,000.</summary>
    public const int DefaultMaxProperties = 1_000_000;

    /// <summary>The default of <see cref="MaxParametersPerProperty"/>: 100.</summary>
    public const int DefaultMaxParametersPerProperty = 100;

    /// <summary>The default of <see cref="MaxErrors"/>: 10,000.</summary>
    public const int DefaultMaxErrors = 10_000;

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
    /// The most lines of all vCards of the file together, each parameter value of a line counting as one more (FR-073,
    /// FR-090): what bounds the memory of a result, since a line of a few bytes costs a property of 60 to 150 and an
    /// empty parameter of three bytes costs up to 70 more. Reading stops with
    /// <see cref="ContactErrorCodes.TooManyPropertiesInFile"/> and keeps the entries before the card being read.
    /// </summary>
    /// <remarks>
    /// <c>EMAIL;TYPE=INTERNET,HOME:…</c> counts three. A real export of 10,000 contacts counts some 300,000 lines and as
    /// many parameter values again, well under the default.
    /// </remarks>
    public int MaxProperties { get; set; } = DefaultMaxProperties;

    /// <summary>
    /// The most parameter values of one line (FR-090), each value of a list (<c>TYPE=HOME,WORK</c>) and each bare
    /// parameter (<c>TEL;WORK;VOICE</c>) counting one. A line with more is dropped with a
    /// <see cref="ContactErrorCodes.TooManyParameters"/> error, without tokenizing the rest of it; the card is read on.
    /// </summary>
    public int MaxParametersPerProperty { get; set; } = DefaultMaxParametersPerProperty;

    /// <summary>
    /// The most errors a result lists (FR-074). The read goes on; further errors are counted in
    /// <see cref="ContactReadResult.OmittedErrorCount"/> and named by one <see cref="ContactErrorCodes.TooManyErrors"/>
    /// file error.
    /// </summary>
    public int MaxErrors { get; set; } = DefaultMaxErrors;

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
        if (MaxProperties <= 0) throw new ArgumentOutOfRangeException(nameof(MaxProperties), MaxProperties, "The file's property limit must be positive.");
        if (MaxParametersPerProperty <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxParametersPerProperty), MaxParametersPerProperty, "The parameter limit must be positive.");
        if (MaxErrors < 0) throw new ArgumentOutOfRangeException(nameof(MaxErrors), MaxErrors, "The error limit must not be negative.");
        if (MaxLineBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxLineBytes), MaxLineBytes, "The line limit must be positive.");
        if (MaxPhotoBytes <= 0) throw new ArgumentOutOfRangeException(nameof(MaxPhotoBytes), MaxPhotoBytes, "The photo limit must be positive.");
        if (string.IsNullOrWhiteSpace(FallbackEncoding))
            throw new InvalidOperationException("The fallback encoding of the contact read options is empty.");

        return CodePages.Get(FallbackEncoding);
    }
}
