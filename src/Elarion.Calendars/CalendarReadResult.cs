using System.Globalization;
using Elarion.Abstractions;

namespace Elarion.Calendars;

/// <summary>
/// The result of reading a calendar (spec 0004 FR-012, FR-013, FR-024): the calendar's own properties, every event
/// and to-do with its value or its errors, the errors about the file, and the component tree.
/// </summary>
/// <example>
/// <code>
/// var result = await reader.ReadAsync(request.File.OpenReadStream(), cancellationToken: ct);
/// if (result.FileErrors.Count &gt; 0) return result.ToAppError("file");
/// var preview = result.Events.Select(e => new PreviewRow(e.Index, e.Value?.Summary, e.Value?.Start, e.Errors)).ToList();
/// </code>
/// </example>
public sealed class CalendarReadResult {
    /// <summary>The default of <see cref="ToAppError"/>'s message cap: 100.</summary>
    public const int DefaultMaxMessages = 100;

    private readonly CalendarExpandOptions? _expandDefaults;

    /// <summary>Creates a result, for a handler's tests.</summary>
    public CalendarReadResult(
        CalendarHeader calendar,
        IReadOnlyList<CalendarEntry<CalendarEvent>> events,
        IReadOnlyList<CalendarEntry<CalendarTodo>> todos,
        IReadOnlyList<CalendarError>? fileErrors = null,
        CalendarComponent? root = null,
        string? byteOrderMarkEncoding = null,
        bool usedFallbackEncoding = false)
        : this(calendar, events, todos, fileErrors, root, byteOrderMarkEncoding, usedFallbackEncoding, null) { }

    internal CalendarReadResult(
        CalendarHeader calendar,
        IReadOnlyList<CalendarEntry<CalendarEvent>> events,
        IReadOnlyList<CalendarEntry<CalendarTodo>> todos,
        IReadOnlyList<CalendarError>? fileErrors,
        CalendarComponent? root,
        string? byteOrderMarkEncoding,
        bool usedFallbackEncoding,
        CalendarExpandOptions? expandDefaults) {
        ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(todos);
        Calendar = calendar;
        Events = events;
        Todos = todos;
        FileErrors = fileErrors ?? [];
        Root = root;
        ByteOrderMarkEncoding = byteOrderMarkEncoding;
        UsedFallbackEncoding = usedFallbackEncoding;
        _expandDefaults = expandDefaults;
    }

    /// <summary>The calendar's own properties.</summary>
    public CalendarHeader Calendar { get; }

    /// <summary>The <c>VCALENDAR</c> component tree, or <see langword="null"/> when the file holds none (FR-041).</summary>
    public CalendarComponent? Root { get; }

    /// <summary>Every <c>VEVENT</c>, in file order.</summary>
    public IReadOnlyList<CalendarEntry<CalendarEvent>> Events { get; }

    /// <summary>Every <c>VTODO</c>, in file order.</summary>
    public IReadOnlyList<CalendarEntry<CalendarTodo>> Todos { get; }

    /// <summary>The errors about the file: unreadable, too large, too many components, and lines outside every item.</summary>
    public IReadOnlyList<CalendarError> FileErrors { get; }

    /// <summary>Every error: the file errors, then the items' in line order, including the ones the application added.</summary>
    public IEnumerable<CalendarError> Errors => FileErrors.Concat(
        Events.Select(static e => (e.LineNumber, e.Errors)).Concat(Todos.Select(static t => (t.LineNumber, t.Errors)))
            .OrderBy(static e => e.LineNumber)
            .SelectMany(static e => e.Errors));

    /// <summary>Whether the result has any error, including one the application added.</summary>
    public bool HasErrors => FileErrors.Count > 0 || Events.Any(static e => e.Errors.Count > 0) || Todos.Any(static t => t.Errors.Count > 0);

    /// <summary>The encoding a byte-order mark chose (<c>utf-8</c>, <c>utf-16le</c>, <c>utf-16be</c>), or <see langword="null"/>.</summary>
    public string? ByteOrderMarkEncoding { get; }

    /// <summary>Whether the fallback encoding decoded any line (FR-005).</summary>
    public bool UsedFallbackEncoding { get; }

    /// <summary>The typed items of every entry that has one, events then to-dos.</summary>
    public IEnumerable<CalendarItem> Items =>
        Events.Where(static e => e.HasValue).Select(static e => (CalendarItem)e.Value!)
            .Concat(Todos.Where(static t => t.HasValue).Select(static t => (CalendarItem)t.Value!));

    /// <summary>
    /// The occurrences of every item that overlap <c>[<paramref name="from"/>, <paramref name="to"/>)</c>
    /// (FR-030), with the expand defaults of the options the reader was configured with when none are passed.
    /// </summary>
    public CalendarOccurrences Expand(DateTimeOffset from, DateTimeOffset to, CalendarExpandOptions? options = null) =>
        CalendarRecurrence.Expand(Items, from, to, options ?? _expandDefaults);

    /// <summary>
    /// Converts the errors to an <see cref="ErrorKind.Validation"/> <see cref="AppError"/> whose field errors are keyed by
    /// <paramref name="fieldPath"/> (FR-024): normally the uploaded-file member, such as <c>file</c>.
    /// </summary>
    /// <param name="fieldPath">The wire-named field path of the upload, or <see langword="null"/> for the empty key.</param>
    /// <param name="format">
    /// Words one error, for example in the application's language from <see cref="CalendarError.Code"/> and its data;
    /// <see langword="null"/> uses <see cref="CalendarError.ToString"/>, which names the item, the line and the property.
    /// </param>
    /// <param name="maxMessages">The most messages; the rest are counted in one closing message.</param>
    /// <exception cref="InvalidOperationException">The result has no error.</exception>
    public AppError ToAppError(string? fieldPath = null, Func<CalendarError, string>? format = null, int maxMessages = DefaultMaxMessages) {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        var errors = Errors.ToList();
        if (errors.Count == 0) throw new InvalidOperationException("The calendar read result has no error to convert.");

        format ??= static error => error.ToString();
        var messages = errors.Take(maxMessages).Select(format).ToList();
        if (errors.Count > maxMessages) {
            var rest = errors.Count - maxMessages;
            messages.Add(rest == 1 ? "1 more problem is not listed." : $"{rest.ToString(CultureInfo.InvariantCulture)} more problems are not listed.");
        }

        var count = errors.Count == 1 ? "1 problem" : $"{errors.Count.ToString(CultureInfo.InvariantCulture)} problems";
        return AppError.Validation(
            $"The calendar has {count}.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [fieldPath ?? ""] = [.. messages] });
    }
}
