using Elarion.ContentLines;

namespace Elarion.Contacts;

/// <summary>
/// One property of a vCard as the file states it (FR-019, FR-067): the lossless layer under <see cref="Contact"/>.
/// </summary>
/// <param name="Group">The group prefix, such as <c>item1</c> of <c>item1.TEL</c>, or <see langword="null"/>.</param>
/// <param name="Name">The name, in upper case when read.</param>
/// <param name="Parameters">The parameters in file order; a bare vCard 2.1 parameter reads as a <c>TYPE</c> or <c>ENCODING</c> value.</param>
/// <param name="Value">
/// The value as written: decoded from its transfer encoding (quoted-printable) and its character set into text, but
/// still escaped (<c>\,</c>, <c>\;</c>, <c>\n</c>), so a structured value can still be split. A line break inside a
/// quoted-printable value reads as <c>\n</c>. A vCard 2.1 value, which escapes only <c>\;</c>, is given in this 3.0
/// escaping: its other backslashes doubled (FR-072). A Base64 value stays Base64 text; <see cref="GetText"/> unescapes.
/// </param>
/// <remarks>
/// Equality ignores <see cref="LineNumber"/>, compares names, groups and parameter names ignoring case, and compares the
/// parameters element by element, so a property written and read back equals the one written.
/// </remarks>
public sealed record ContactProperty(string? Group, string Name, IReadOnlyList<ContactParameter> Parameters, string Value) {
    /// <summary>The line of the file the property starts on; set when read, <see langword="null"/> for one built in code.</summary>
    public int? LineNumber { get; init; }

    /// <summary>Creates a property without a group and parameters.</summary>
    public ContactProperty(string name, string value) : this(null, name, [], value) {
    }

    /// <summary>The values of the first parameter named <paramref name="name"/>, ignoring case; empty when there is none.</summary>
    public IReadOnlyList<string> GetParameter(string name) {
        foreach (var parameter in Parameters)
            if (string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase)) return parameter.Values;

        return [];
    }

    /// <summary>
    /// The value as one text, unescaped (FR-078): <c>\,</c>, <c>\;</c>, <c>\:</c> and <c>\\</c> read as the character,
    /// <c>\n</c> as a line break. For a second <c>NOTE</c> or an <c>X-SOCIALPROFILE</c> the model does not map.
    /// </summary>
    public string GetText() => ContentText.Unescape(Value);

    /// <summary>The value as a list of texts split at unescaped commas, each unescaped, as <c>CATEGORIES</c> holds them.</summary>
    public IReadOnlyList<string> GetTexts() => ContentText.SplitUnescape(Value, ',');

    /// <inheritdoc/>
    public bool Equals(ContactProperty? other) {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Group ?? "", other.Group ?? "", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Value, other.Value, StringComparison.Ordinal)
            && Parameters.SequenceEqual(other.Parameters);
    }

    /// <inheritdoc/>
    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(Group ?? ""),
            StringComparer.OrdinalIgnoreCase.GetHashCode(Name),
            Value,
            Parameters.Count);
}

/// <summary>One parameter of a <see cref="ContactProperty"/>, its values unquoted and RFC 6868 carets decoded.</summary>
/// <param name="Name">The name, in upper case when read.</param>
/// <param name="Values">The values: repeated and comma-joined values of one name are kept apart.</param>
public sealed record ContactParameter(string Name, IReadOnlyList<string> Values) {
    /// <summary>Creates a parameter with one value.</summary>
    public ContactParameter(string name, string value) : this(name, [value]) {
    }

    /// <inheritdoc/>
    public bool Equals(ContactParameter? other) {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) && Values.SequenceEqual(other.Values, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(Name), Values.Count);
}
