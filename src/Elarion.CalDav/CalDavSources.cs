using Elarion.Abstractions;
using Elarion.Calendars;

namespace Elarion.CalDav;

/// <summary>
/// A calendar source (ADR-0111, spec 0006 FR-010): a query request whose handler returns the calendars of the source
/// the current user may see. The handler is an ordinary handler, so <c>[RequirePermission]</c>, resource rules,
/// feature gates and module enablement decide what a calendar app shows exactly as they decide what the application
/// shows. Register it with <c>AddElarionCalDavSource&lt;TSelf&gt;()</c> in the module's <c>ConfigureServices</c>.
/// </summary>
/// <typeparam name="TSelf">The request type itself.</typeparam>
/// <example>
/// <code>
/// public sealed record MyShiftCalendars : ICalDavSource&lt;MyShiftCalendars&gt; {
///     public static string SourceName => "shifts";
/// }
///
/// [RequirePermission("shifts", Verbs.Read)]
/// public sealed class ListMyShiftCalendars(ICurrentUser user) : IHandler&lt;MyShiftCalendars, Result&lt;CalDavSourceListing&gt;&gt; {
///     public ValueTask&lt;Result&lt;CalDavSourceListing&gt;&gt; HandleAsync(MyShiftCalendars query, CancellationToken ct) =&gt;
///         ValueTask.FromResult(Result&lt;CalDavSourceListing&gt;.Success(new CalDavSourceListing([
///             new PublishedCalendarInfo($"u-{user.UserId}", "My shifts") { Color = "#2E7D32" },
///         ])));
/// }
/// </code>
/// </example>
public interface ICalDavSource<TSelf> : IQuery<TSelf, CalDavSourceListing> where TSelf : ICalDavSource<TSelf> {
    /// <summary>
    /// The source's name: 1 to 64 ASCII letters, digits, <c>-</c> and <c>_</c>, unique among the application's sources.
    /// It is the first part of each calendar's URL segment, <c>{source}.{key}</c>.
    /// </summary>
    static abstract string SourceName { get; }
}

/// <summary>The calendars of a source the current user may see, as the source's handler returns them.</summary>
/// <param name="Calendars">The calendars; their keys are unique within the source.</param>
public sealed record CalDavSourceListing(IReadOnlyList<PublishedCalendarInfo> Calendars) {
    /// <summary>No calendars.</summary>
    public static CalDavSourceListing Empty { get; } = new([]);
}

/// <summary>
/// A published calendar as a source describes it to the user who may see it: its key within the source, its name and,
/// optionally, its color, description, component kinds and time zone.
/// </summary>
/// <param name="Key">The key within the source: 1 to 64 ASCII letters, digits, <c>-</c> and <c>_</c>.</param>
/// <param name="DisplayName">The name calendar apps show.</param>
public sealed record PublishedCalendarInfo(string Key, string DisplayName) {
    /// <summary>The color as <c>#RRGGBB</c> or <c>#RRGGBBAA</c> (Apple's <c>calendar-color</c>), or <see langword="null"/>.</summary>
    public string? Color { get; init; }

    /// <summary>The description (<c>calendar-description</c>), or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>The component kinds the calendar holds (<c>supported-calendar-component-set</c>). Default events.</summary>
    public CalendarComponentKinds Components { get; init; } = CalendarComponentKinds.Event;

    /// <summary>
    /// The calendar's time zone (<c>calendar-timezone</c>), in which floating times are evaluated; <see langword="null"/>
    /// for UTC.
    /// </summary>
    public TimeZoneInfo? TimeZone { get; init; }

    /// <summary>
    /// The calendar's URL segment in the user's home, or <see langword="null"/> for <c>{source}.{key}</c>: 1 to 128 ASCII
    /// letters, digits, <c>-</c>, <c>_</c>, <c>.</c>, <c>~</c> or <c>@</c>, unique among the calendars the user sees. Set it
    /// to keep URLs that devices already synced; changing it makes every device sync the calendar anew.
    /// </summary>
    public string? Segment { get; init; }
}

/// <summary>The address of a published calendar: the source that declares it and its key within the source.</summary>
public sealed record CalDavAddress {
    /// <summary>The longest source name or key: 64 characters.</summary>
    public const int MaxSegmentLength = Elarion.WebDav.DavSources.MaxSegmentLength;

    /// <summary>Creates an address.</summary>
    /// <exception cref="ArgumentException">A name is not 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</exception>
    public CalDavAddress(string source, string key) {
        if (!IsValidSegment(source)) throw new ArgumentException($"The calendar source name '{source}' must be 1 to 64 ASCII letters, digits, '-' or '_'.", nameof(source));
        if (!IsValidSegment(key)) throw new ArgumentException($"The calendar key '{key}' of source '{source}' must be 1 to 64 ASCII letters, digits, '-' or '_'.", nameof(key));
        Source = source;
        Key = key;
    }

    /// <summary>The source name.</summary>
    public string Source { get; }

    /// <summary>The key within the source.</summary>
    public string Key { get; }

    /// <summary>The address in the store.</summary>
    public Elarion.WebDav.DavCollectionAddress ToCollectionAddress() => new(CalDavProtocol.Kind, Source, Key);

    /// <inheritdoc />
    public override string ToString() => Source + "." + Key;

    /// <summary>Whether <paramref name="value"/> is 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static bool IsValidSegment(string? value) => Elarion.WebDav.DavSources.IsValidSegment(value);
}

/// <summary>What the shared source machinery calls a calendar source in its messages.</summary>
internal static class CalDavSourceKind {
    public static Elarion.WebDav.DavSourceKind Value { get; } = new("calendar source", "AddElarionCalDavSource");
}
