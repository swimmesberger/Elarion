namespace Elarion.Contacts;

/// <summary>
/// The structured name of a contact (<c>N</c>, FR-020). Several values of one part, comma-separated in the file, are
/// joined with <c>", "</c>: <c>N:Huber;Anna,Maria</c> reads the given name <c>Anna, Maria</c>, and is written back as
/// one value with its comma escaped.
/// </summary>
/// <param name="Family">The family name.</param>
/// <param name="Given">The given name.</param>
/// <param name="Additional">The additional names.</param>
/// <param name="Prefix">The honorific prefix, such as <c>Dr.</c>.</param>
/// <param name="Suffix">The honorific suffix, such as <c>Jr.</c>.</param>
public sealed record ContactName(string? Family, string? Given, string? Additional, string? Prefix, string? Suffix) {
    /// <summary>The parts that are not empty, in reading order: prefix, given, additional, family, suffix.</summary>
    public override string ToString() =>
        string.Join(' ', new[] { Prefix, Given, Additional, Family, Suffix }.Where(static part => !string.IsNullOrWhiteSpace(part)));
}

/// <summary>A phone number of a contact (<c>TEL</c>, FR-021).</summary>
/// <param name="Number">The number as written, without invisible directional marks (FR-018).</param>
public sealed record ContactPhone(string Number) {
    /// <summary>The types.</summary>
    public ContactTypes Types { get; init; }

    /// <summary>Whether the number is marked preferred.</summary>
    public bool Preferred { get; init; }

    /// <summary>The custom label, such as Apple's <c>X-ABLabel</c> of the number's group.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The line of the file the value was read from (FR-077); <see langword="null"/> for a value built in code. Equality
    /// ignores it, and the writer does not write it.
    /// </summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// The group prefix it was read with, such as <c>item1</c>, to find the raw <c>X-ABLabel</c> among the entry's
    /// properties (FR-077); <see langword="null"/> without one. Equality ignores it, and the writer assigns its own groups.
    /// </summary>
    public string? Group { get; init; }

    /// <inheritdoc/>
    public bool Equals(ContactPhone? other) =>
        other is not null && Number == other.Number && Types == other.Types && Preferred == other.Preferred && Label == other.Label;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Number, Types, Preferred, Label);
}

/// <summary>An e-mail address of a contact (<c>EMAIL</c>, FR-021).</summary>
/// <param name="Address">The address.</param>
public sealed record ContactEmail(string Address) {
    /// <summary>The types.</summary>
    public ContactTypes Types { get; init; }

    /// <summary>Whether the address is marked preferred.</summary>
    public bool Preferred { get; init; }

    /// <summary>The custom label, such as Apple's <c>X-ABLabel</c> of the address's group.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The line of the file the value was read from (FR-077); <see langword="null"/> for a value built in code. Equality
    /// ignores it, and the writer does not write it.
    /// </summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// The group prefix it was read with, such as <c>item1</c>, to find the raw <c>X-ABLabel</c> among the entry's
    /// properties (FR-077); <see langword="null"/> without one. Equality ignores it, and the writer assigns its own groups.
    /// </summary>
    public string? Group { get; init; }

    /// <inheritdoc/>
    public bool Equals(ContactEmail? other) =>
        other is not null && Address == other.Address && Types == other.Types && Preferred == other.Preferred && Label == other.Label;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Address, Types, Preferred, Label);
}

/// <summary>A web address of a contact (<c>URL</c>, FR-021).</summary>
/// <param name="Value">The address as written.</param>
public sealed record ContactUrl(string Value) {
    /// <summary>The types.</summary>
    public ContactTypes Types { get; init; }

    /// <summary>Whether the address is marked preferred.</summary>
    public bool Preferred { get; init; }

    /// <summary>The custom label, such as Apple's <c>X-ABLabel</c> of the address's group.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The line of the file the value was read from (FR-077); <see langword="null"/> for a value built in code. Equality
    /// ignores it, and the writer does not write it.
    /// </summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// The group prefix it was read with, such as <c>item1</c>, to find the raw <c>X-ABLabel</c> among the entry's
    /// properties (FR-077); <see langword="null"/> without one. Equality ignores it, and the writer assigns its own groups.
    /// </summary>
    public string? Group { get; init; }

    /// <inheritdoc/>
    public bool Equals(ContactUrl? other) =>
        other is not null && Value == other.Value && Types == other.Types && Preferred == other.Preferred && Label == other.Label;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Value, Types, Preferred, Label);
}

/// <summary>
/// A postal address of a contact (<c>ADR</c>, FR-020): its seven standard components; a further component is ignored
/// (FR-026).
/// </summary>
public sealed record ContactAddress {
    /// <summary>The post office box.</summary>
    public string? PostOfficeBox { get; init; }

    /// <summary>The extended address, such as an apartment.</summary>
    public string? Extended { get; init; }

    /// <summary>The street address.</summary>
    public string? Street { get; init; }

    /// <summary>The locality, such as a city.</summary>
    public string? Locality { get; init; }

    /// <summary>The region, such as a state.</summary>
    public string? Region { get; init; }

    /// <summary>The postal code.</summary>
    public string? PostalCode { get; init; }

    /// <summary>The country.</summary>
    public string? Country { get; init; }

    /// <summary>The types.</summary>
    public ContactTypes Types { get; init; }

    /// <summary>Whether the address is marked preferred.</summary>
    public bool Preferred { get; init; }

    /// <summary>The custom label, such as Apple's <c>X-ABLabel</c> of the address's group.</summary>
    public string? Label { get; init; }

    /// <summary>
    /// The line of the file the value was read from (FR-077); <see langword="null"/> for a value built in code. Equality
    /// ignores it, and the writer does not write it.
    /// </summary>
    public int? LineNumber { get; init; }

    /// <summary>
    /// The group prefix it was read with, such as <c>item1</c>, to find the raw <c>X-ABLabel</c> among the entry's
    /// properties (FR-077); <see langword="null"/> without one. Equality ignores it, and the writer assigns its own groups.
    /// </summary>
    public string? Group { get; init; }

    /// <inheritdoc/>
    public bool Equals(ContactAddress? other) =>
        other is not null && PostOfficeBox == other.PostOfficeBox && Extended == other.Extended && Street == other.Street
        && Locality == other.Locality && Region == other.Region && PostalCode == other.PostalCode && Country == other.Country
        && Types == other.Types && Preferred == other.Preferred && Label == other.Label;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(Street, Locality, PostalCode, Country), Types, Preferred, Label);
}
