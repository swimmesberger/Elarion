namespace Elarion.Calendars;

/// <summary>
/// One component of the lossless component tree (spec 0004 FR-041, FR-042): its name, its properties and its
/// sub-components in the order the file states them, and the line it starts on.
/// </summary>
/// <remarks>
/// The tree is mutable and has public constructors, so a caller can assemble, prune or rewrite a calendar and hand it
/// to <see cref="CalendarWriter"/>; <see cref="Clone"/> copies it deeply. A tree is not thread-safe.
/// </remarks>
/// <example>
/// <code>
/// var copy = entry.Component.Clone();
/// copy.Properties.Remove(copy.Properties.First(p => p.Name == "DESCRIPTION"));
/// await CalendarWriter.WriteAsync(stream, new CalendarComponent("VCALENDAR") { Components = { copy } });
/// </code>
/// </example>
public sealed class CalendarComponent {
    /// <summary>Creates an empty component.</summary>
    /// <param name="name">The component name, such as <c>VEVENT</c>; it is kept in upper case.</param>
    public CalendarComponent(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.ToUpperInvariant();
    }

    /// <summary>The component name in upper case, such as <c>VEVENT</c>.</summary>
    public string Name { get; }

    /// <summary>The properties, in file order.</summary>
    public IList<CalendarProperty> Properties { get; } = new List<CalendarProperty>();

    /// <summary>The sub-components, in file order.</summary>
    public IList<CalendarComponent> Components { get; } = new List<CalendarComponent>();

    /// <summary>The line of the component's <c>BEGIN</c>, when it was read.</summary>
    public int? LineNumber { get; set; }

    /// <summary>The first property named <paramref name="name"/>, ignoring case, or <see langword="null"/>.</summary>
    public CalendarProperty? FindProperty(string name) {
        foreach (var property in Properties)
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property;

        return null;
    }

    /// <summary>The properties named <paramref name="name"/>, ignoring case, in file order.</summary>
    public IEnumerable<CalendarProperty> FindProperties(string name) =>
        Properties.Where(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A deep copy of the component, its properties, parameters and sub-components.</summary>
    public CalendarComponent Clone() {
        var copy = new CalendarComponent(Name) { LineNumber = LineNumber };
        foreach (var property in Properties) copy.Properties.Add(property.Clone());
        foreach (var component in Components) copy.Components.Add(component.Clone());
        return copy;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>
/// One parameter of a <see cref="CalendarProperty"/>: its name and its values, decoded (quotes removed, RFC 6868
/// carets decoded).
/// </summary>
public sealed class CalendarParameter {
    /// <summary>Creates a parameter.</summary>
    /// <param name="name">The parameter name, such as <c>TZID</c>; it is kept in upper case.</param>
    /// <param name="values">The values.</param>
    public CalendarParameter(string name, params IEnumerable<string> values) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(values);
        Name = name.ToUpperInvariant();
        Values = [.. values];
    }

    /// <summary>The parameter name in upper case.</summary>
    public string Name { get; }

    /// <summary>The values, decoded.</summary>
    public IList<string> Values { get; }

    /// <summary>The first value, or <see langword="null"/> when there is none.</summary>
    public string? Value => Values.Count > 0 ? Values[0] : null;

    /// <summary>A copy of the parameter.</summary>
    public CalendarParameter Clone() => new(Name, Values);

    /// <inheritdoc/>
    public override string ToString() => $"{Name}={string.Join(',', Values)}";
}
