using System.Diagnostics.CodeAnalysis;

namespace Elarion.Calendars;

/// <summary>
/// One event or to-do of a read (spec 0004 FR-013): its index, the line it starts on, its lossless component, its
/// typed value when it has one, and its errors — so a preview lists every item, readable or not.
/// </summary>
/// <typeparam name="T">The typed item: <see cref="CalendarEvent"/> or <see cref="CalendarTodo"/>.</typeparam>
/// <remarks>
/// <see cref="HasValue"/> is not "valid": an item with an error in an optional property, or with an error the
/// application added, keeps its value; only a broken time, a missing <c>DTSTART</c> or a per-item limit clears it
/// (FR-014). <see cref="AddError"/> is the one mutation; an entry is not thread-safe.
/// </remarks>
public sealed class CalendarEntry<T> where T : CalendarItem {
    private readonly List<CalendarError> _errors;

    /// <summary>Creates an entry, for a handler's tests.</summary>
    /// <param name="index">The 1-based index among the calendar's events and to-dos.</param>
    /// <param name="lineNumber">The line the item starts on.</param>
    /// <param name="component">The item's component.</param>
    /// <param name="value">The typed item, or <see langword="null"/>.</param>
    /// <param name="errors">The item's errors.</param>
    public CalendarEntry(int index, int lineNumber, CalendarComponent component, T? value, IEnumerable<CalendarError>? errors = null) {
        ArgumentNullException.ThrowIfNull(component);
        Index = index;
        LineNumber = lineNumber;
        Component = component;
        Value = value;
        _errors = errors is null ? [] : [.. errors];
    }

    /// <summary>The 1-based index among the calendar's events and to-dos, counted together in file order.</summary>
    public int Index { get; }

    /// <summary>The line of the item's <c>BEGIN</c>.</summary>
    public int LineNumber { get; }

    /// <summary>The item's component, with every property and sub-component the file states (FR-041).</summary>
    public CalendarComponent Component { get; }

    /// <summary>The typed item, when its times could be read.</summary>
    public T? Value { get; }

    /// <summary>Whether the entry has a typed item.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    public bool HasValue => Value is not null;

    /// <summary>The item's errors, the package's then the application's.</summary>
    public IReadOnlyList<CalendarError> Errors => _errors;

    /// <summary>The item's <c>UID</c>, from its value or its component.</summary>
    public string? Uid => Value?.Uid ?? Component.FindProperty("UID")?.GetText();

    /// <summary>
    /// Adds the application's own error (FR-023), located like the package's: the item, its line, and optionally the
    /// property and its line. The value stays; the error appears in <see cref="CalendarReadResult.Errors"/> and its
    /// <c>AppError</c>.
    /// </summary>
    /// <param name="code">The application's stable code, with its own prefix, such as <c>import.date_in_past</c>.</param>
    /// <param name="message">The message, without the location.</param>
    /// <param name="property">The property the error is about, such as <c>DTSTART</c>.</param>
    /// <param name="lineNumber">The line the error is about; by default the property's line when the item has it.</param>
    /// <example>
    /// <code>
    /// foreach (var entry in result.Events.Where(e => e.HasValue))
    ///     if (entry.Value!.Start!.Value.Date &lt; today)
    ///         entry.AddError("import.date_in_past", "The event lies in the past.", "DTSTART");
    /// </code>
    /// </example>
    public void AddError(string code, string message, string? property = null, int? lineNumber = null) {
        ArgumentException.ThrowIfNullOrEmpty(code);
        ArgumentNullException.ThrowIfNull(message);
        var found = property is null ? null : Component.FindProperty(property);
        _errors.Add(new CalendarError(code, message) {
            ItemIndex = Index,
            ItemLineNumber = LineNumber,
            LineNumber = lineNumber ?? found?.LineNumber,
            Property = property?.ToUpperInvariant(),
            Value = found?.RawValue,
            Component = Component.Name,
            Uid = Uid,
        });
    }

    internal void Add(CalendarError error) => _errors.Add(error.Locate(Index, LineNumber, Component.Name, Uid));
}
