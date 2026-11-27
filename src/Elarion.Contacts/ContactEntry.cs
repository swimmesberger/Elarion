using System.Diagnostics.CodeAnalysis;

namespace Elarion.Contacts;

/// <summary>
/// One vCard of a read (FR-019): its index, the line of its <c>BEGIN</c>, its version, every property in file order,
/// its contact and its errors. An error does not take the contact away; only a card with nothing usable left, or with
/// more lines than the limit, has none.
/// </summary>
/// <remarks>
/// <see cref="HasValue"/> is not <c>IsValid</c>: an entry with errors keeps its contact, and the application decides
/// what an error means for it. <see cref="AddError"/> is the one mutation: the handler adds its own errors (a duplicate,
/// an unknown key) before it builds its preview or <see cref="ContactReadResult.ToAppError"/>. An entry is not
/// thread-safe.
/// </remarks>
public sealed class ContactEntry {
    private readonly List<ContactError> _errors;

    /// <summary>Creates an entry by hand, for a handler's tests.</summary>
    /// <param name="index">The 1-based index of the vCard in the file.</param>
    /// <param name="lineNumber">The line of its <c>BEGIN</c>.</param>
    /// <param name="version">The version it states, such as <c>3.0</c>.</param>
    /// <param name="properties">Its properties in file order.</param>
    /// <param name="value">Its contact, or <see langword="null"/>.</param>
    /// <param name="errors">Its errors.</param>
    public ContactEntry(
        int index,
        int lineNumber,
        string version,
        IReadOnlyList<ContactProperty> properties,
        Contact? value,
        IEnumerable<ContactError>? errors = null) {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(properties);
        Index = index;
        LineNumber = lineNumber;
        Version = version;
        Properties = properties;
        Value = value;
        _errors = errors is null ? [] : [.. errors];
    }

    /// <summary>The 1-based index of the vCard in the file.</summary>
    public int Index { get; }

    /// <summary>The line of the vCard's <c>BEGIN</c>.</summary>
    public int LineNumber { get; }

    /// <summary>The version the vCard states (<c>2.1</c>, <c>3.0</c>, <c>4.0</c>), or <c>3.0</c> when it states none.</summary>
    public string Version { get; }

    /// <summary>
    /// Every property of the vCard between its <c>BEGIN</c> and its <c>END</c>, in file order, with its group,
    /// parameters, value as written and line number, whether the contact maps it or not (FR-019); a line that could not
    /// be kept (too long, malformed) is missing.
    /// </summary>
    public IReadOnlyList<ContactProperty> Properties { get; }

    /// <summary>The contact, or <see langword="null"/> when nothing usable is left or the card has too many lines.</summary>
    public Contact? Value { get; }

    /// <summary>Whether the entry has a contact; errors may exist either way.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool HasValue => Value is not null;

    /// <summary>The entry's errors, including those the handler added.</summary>
    public IReadOnlyList<ContactError> Errors => _errors;

    /// <summary>
    /// Adds an application error, located like the package's own: the entry's index and line, the contact's display
    /// name, and the property and line when given (FR-044). It does not remove the contact.
    /// </summary>
    /// <param name="code">The application's own code, such as <c>import.duplicate</c>.</param>
    /// <param name="message">The English default message, without the location.</param>
    /// <param name="property">The property concerned, such as <c>EMAIL</c>, or <see langword="null"/>.</param>
    /// <param name="lineNumber">The line concerned, or <see langword="null"/>.</param>
    /// <returns>The error added.</returns>
    public ContactError AddError(string code, string message, string? property = null, int? lineNumber = null) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);

        string? value = null;
        if (property is not null) {
            foreach (var candidate in Properties) {
                if (!string.Equals(candidate.Name, property, StringComparison.OrdinalIgnoreCase)) continue;
                if (lineNumber is not null && candidate.LineNumber != lineNumber) continue;
                value = candidate.Value;
                lineNumber ??= candidate.LineNumber;
                break;
            }
        }

        var error = new ContactError(code, message, Index, LineNumber, lineNumber, property, value, displayName: Value?.DisplayName);
        _errors.Add(error);
        return error;
    }
}
