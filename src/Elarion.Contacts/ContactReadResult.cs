using System.Globalization;
using Elarion.Abstractions;

namespace Elarion.Contacts;

/// <summary>
/// The result of reading contacts (ADR-0110, decision 4): every vCard of the file as an entry with its contact and its
/// errors, the errors about the file, and how its text was decoded — so a handler builds a preview of every contact a
/// person exported before anything is saved.
/// </summary>
/// <remarks>The constructor is public so a handler's tests can build a result by hand.</remarks>
/// <example>
/// <code>
/// var result = await reader.ReadAsync(request.File.OpenReadStream(), cancellationToken: ct);
/// foreach (var entry in result.Entries.Where(entry => entry.HasValue))
///     if (await db.People.AnyAsync(p => p.Email == entry.Value.Emails.FirstOrDefault()!.Address, ct))
///         entry.AddError("import.duplicate", "The contact is already stored.");
/// if (result.HasErrors) return result.ToAppError("contacts");
/// </code>
/// </example>
public sealed class ContactReadResult {
    /// <summary>The default of <see cref="ToAppError"/>'s message cap: 100.</summary>
    public const int DefaultMaxMessages = 100;

    /// <summary>Creates a result.</summary>
    /// <param name="entries">The entries, in file order.</param>
    /// <param name="fileErrors">The errors about the file.</param>
    /// <param name="byteOrderMarkEncoding">The encoding a byte-order mark chose, or <see langword="null"/>.</param>
    /// <param name="usedFallbackEncoding">Whether the fallback encoding decoded any line.</param>
    public ContactReadResult(
        IReadOnlyList<ContactEntry> entries,
        IReadOnlyList<ContactError>? fileErrors = null,
        string? byteOrderMarkEncoding = null,
        bool usedFallbackEncoding = false) {
        ArgumentNullException.ThrowIfNull(entries);
        Entries = entries;
        FileErrors = fileErrors ?? [];
        ByteOrderMarkEncoding = byteOrderMarkEncoding;
        UsedFallbackEncoding = usedFallbackEncoding;
    }

    /// <summary>Every vCard read, with or without a contact (FR-019).</summary>
    public IReadOnlyList<ContactEntry> Entries { get; }

    /// <summary>The errors about the file: unreadable, too large, too many contacts.</summary>
    public IReadOnlyList<ContactError> FileErrors { get; }

    /// <summary>Every error: the file errors, then each entry's in file order, including those the handler added.</summary>
    public IEnumerable<ContactError> Errors => FileErrors.Concat(Entries.SelectMany(static entry => entry.Errors));

    /// <summary>The contacts of the entries that have one, errors or not.</summary>
    public IEnumerable<Contact> Contacts => Entries.Where(static entry => entry.HasValue).Select(static entry => entry.Value!);

    /// <summary>Whether the result has any error, including one the handler added.</summary>
    public bool HasErrors => FileErrors.Count > 0 || Entries.Any(static entry => entry.Errors.Count > 0);

    /// <summary>
    /// The encoding a byte-order mark, or a <c>BEGIN</c> in UTF-16 without one, chose for the whole file (<c>utf-8</c>,
    /// <c>utf-16le</c>, <c>utf-16be</c>), or <see langword="null"/> when each line was decoded on its own (FR-015).
    /// </summary>
    public string? ByteOrderMarkEncoding { get; }

    /// <summary>Whether <see cref="ContactReadOptions.FallbackEncoding"/> decoded any value (FR-015).</summary>
    public bool UsedFallbackEncoding { get; }

    /// <summary>
    /// Converts the errors to an <see cref="ErrorKind.Validation"/> <see cref="AppError"/> whose field errors are keyed by
    /// <paramref name="fieldPath"/> (FR-045): normally the uploaded-file member, such as <c>contacts</c>.
    /// </summary>
    /// <param name="fieldPath">The wire-named field path of the upload, or <see langword="null"/> for the empty key.</param>
    /// <param name="format">
    /// Words one error, for example in the application's language from <see cref="ContactError.Code"/> and its data;
    /// <see langword="null"/> uses <see cref="ContactError.ToString"/>, which names the contact, its display name and the line.
    /// </param>
    /// <param name="maxMessages">
    /// The most messages; the rest are counted in one closing message, so a broken large file does not become a
    /// multi-megabyte response.
    /// </param>
    /// <exception cref="InvalidOperationException">The result has no error.</exception>
    public AppError ToAppError(string? fieldPath = null, Func<ContactError, string>? format = null, int maxMessages = DefaultMaxMessages) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        var errors = Errors.ToList();
        if (errors.Count == 0) throw new InvalidOperationException("The contact read result has no error to convert.");

        format ??= static error => error.ToString();
        var messages = errors.Take(maxMessages).Select(format).ToList();
        if (errors.Count > maxMessages) {
            var rest = errors.Count - maxMessages;
            messages.Add(rest == 1 ? "1 more problem is not listed." : $"{rest.ToString(CultureInfo.InvariantCulture)} more problems are not listed.");
        }

        var count = errors.Count == 1 ? "1 problem" : $"{errors.Count.ToString(CultureInfo.InvariantCulture)} problems";
        return AppError.Validation(
            $"The contact file has {count}.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [fieldPath ?? ""] = [.. messages] });
    }
}
