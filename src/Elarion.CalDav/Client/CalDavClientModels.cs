using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using Elarion.Calendars;

namespace Elarion.CalDav;

/// <summary>The settings of a <see cref="CalDavClient"/>; limits are named <c>Max…</c> as in the format packages (spec 0006 FR-043).</summary>
public sealed record CalDavClientOptions {
    /// <summary>The default of <see cref="MaxResponseBytes"/>: 32 MiB.</summary>
    public const long DefaultMaxResponseBytes = 32L * 1024 * 1024;

    /// <summary>The default of <see cref="MaxXmlDepth"/>: 32.</summary>
    public const int DefaultMaxXmlDepth = 32;

    /// <summary>The default of <see cref="MaxXmlElements"/>: 500,000.</summary>
    public const int DefaultMaxXmlElements = 500_000;

    /// <summary>The default of <see cref="MaxObjectsPerStep"/>: 5,000.</summary>
    public const int DefaultMaxObjectsPerStep = 5_000;

    /// <summary>The default of <see cref="MultigetBatchSize"/>: 100.</summary>
    public const int DefaultMultigetBatchSize = 100;

    /// <summary>The default of <see cref="MaxRedirects"/>: 5.</summary>
    public const int DefaultMaxRedirects = 5;

    /// <summary>
    /// The <c>Authorization</c> header value of each request (spec 0006 FR-040): an app password as Basic, or an OAuth bearer
    /// token the application refreshes (Google). Called once per request; <see langword="null"/> sends none.
    /// </summary>
    /// <example>
    /// <code>
    /// Authorization = ct => ValueTask.FromResult&lt;AuthenticationHeaderValue?&gt;(
    ///     new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{appPassword}")))),
    /// </code>
    /// </example>
    public Func<CancellationToken, ValueTask<AuthenticationHeaderValue?>>? Authorization { get; init; }

    /// <summary>The largest response body read; a larger one is the error <see cref="CalDavErrorCodes.TooLarge"/>.</summary>
    public long MaxResponseBytes { get; init; } = DefaultMaxResponseBytes;

    /// <summary>The deepest element nesting of a response.</summary>
    public int MaxXmlDepth { get; init; } = DefaultMaxXmlDepth;

    /// <summary>The most elements of a response.</summary>
    public int MaxXmlElements { get; init; } = DefaultMaxXmlElements;

    /// <summary>
    /// The most objects one sync step fetches; the rest come in the next step, and <see cref="CalDavSyncStep.HasMore"/>
    /// says so.
    /// </summary>
    public int MaxObjectsPerStep { get; init; } = DefaultMaxObjectsPerStep;

    /// <summary>How many objects one <c>calendar-multiget</c> fetches.</summary>
    public int MultigetBatchSize { get; init; } = DefaultMultigetBatchSize;

    /// <summary>How many redirects one request follows (with the same method, body and credentials).</summary>
    public int MaxRedirects { get; init; } = DefaultMaxRedirects;

    /// <summary>How long one request may take; past it the request throws <see cref="TimeoutException"/>. Default: the <see cref="HttpClient"/>'s.</summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>How calendar data from the server is read; the defaults of <see cref="CalendarReadOptions"/>.</summary>
    public CalendarReadOptions CalendarRead { get; init; } = new();

    /// <summary>The clock for the <c>DTSTAMP</c> of objects written without one. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    internal void Validate() {
        if (MaxResponseBytes < 1024) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes), MaxResponseBytes, "Must be at least 1024.");
        if (MaxXmlDepth < 4) throw new ArgumentOutOfRangeException(nameof(MaxXmlDepth), MaxXmlDepth, "Must be at least 4.");
        if (MaxXmlElements < 16) throw new ArgumentOutOfRangeException(nameof(MaxXmlElements), MaxXmlElements, "Must be at least 16.");
        if (MaxObjectsPerStep < 1) throw new ArgumentOutOfRangeException(nameof(MaxObjectsPerStep), MaxObjectsPerStep, "Must be positive.");
        if (MultigetBatchSize < 1) throw new ArgumentOutOfRangeException(nameof(MultigetBatchSize), MultigetBatchSize, "Must be positive.");
        if (MaxRedirects < 0) throw new ArgumentOutOfRangeException(nameof(MaxRedirects), MaxRedirects, "Must not be negative.");
    }
}

/// <summary>The stable error codes of the CalDAV client (spec 0006 FR-041).</summary>
public static class CalDavErrorCodes {
    /// <summary>The server refused the credentials (<c>401</c>).</summary>
    public const string Unauthorized = "caldav.unauthorized";

    /// <summary>The server refused the request for these credentials (<c>403</c>), such as a write to a read-only calendar.</summary>
    public const string Forbidden = "caldav.forbidden";

    /// <summary>The calendar, object or account does not exist (<c>404</c>, <c>410</c>), or discovery found none.</summary>
    public const string NotFound = "caldav.not_found";

    /// <summary>A conditional write failed: someone changed or created the object meanwhile (<c>412</c>).</summary>
    public const string Conflict = "caldav.conflict";

    /// <summary>The server answered with another error status.</summary>
    public const string ServerError = "caldav.server_error";

    /// <summary>The answer is not valid WebDAV.</summary>
    public const string InvalidResponse = "caldav.invalid_response";

    /// <summary>The answer is larger than <see cref="CalDavClientOptions.MaxResponseBytes"/> or holds more elements than allowed.</summary>
    public const string TooLarge = "caldav.too_large";
}

/// <summary>Why a client call failed: a <c>caldav.*</c> code, an English message and its data.</summary>
public sealed record CalDavError {
    /// <summary>The stable code, a constant of <see cref="CalDavErrorCodes"/>.</summary>
    public required string Code { get; init; }

    /// <summary>The English message.</summary>
    public required string Message { get; init; }

    /// <summary>The HTTP status the server answered, when it answered one.</summary>
    public int? StatusCode { get; init; }

    /// <summary>The URL the error is about.</summary>
    public Uri? Url { get; init; }

    /// <summary>The limit that was exceeded, for <see cref="CalDavErrorCodes.TooLarge"/>.</summary>
    public long? Limit { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Code}: {Message}";
}

/// <summary>A client result: a value, or one error (spec 0006 FR-041). Only cancellation, misconfiguration and transport failures throw.</summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed class CalDavResult<T> {
    private CalDavResult(T? value, CalDavError? error) {
        Value = value;
        Error = error;
    }

    /// <summary>The value, when the call succeeded.</summary>
    public T? Value { get; }

    /// <summary>Whether the call succeeded.</summary>
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool HasValue => Error is null;

    /// <summary>The error, when the call failed.</summary>
    public CalDavError? Error { get; }

    /// <summary>A success.</summary>
    public static CalDavResult<T> Success(T value) => new(value, null);

    /// <summary>A failure.</summary>
    public static CalDavResult<T> Failure(CalDavError error) {
        ArgumentNullException.ThrowIfNull(error);
        return new CalDavResult<T>(default, error);
    }

    /// <inheritdoc />
    public override string ToString() => HasValue ? $"Success: {Value}" : $"Failure: {Error}";
}

/// <summary>An account found by <see cref="CalDavClient.DiscoverAsync"/>: its principal, calendar home and calendars.</summary>
public sealed record CalDavAccount {
    /// <summary>The principal URL.</summary>
    public required Uri PrincipalUrl { get; init; }

    /// <summary>The calendar home URL: the first of <see cref="HomeUrls"/>.</summary>
    public required Uri HomeUrl { get; init; }

    /// <summary>
    /// Every calendar home of the principal (RFC 4791 §6.2.1 allows several); <see cref="Calendars"/> holds the calendars of
    /// all of them. When the server names no home, the principal itself.
    /// </summary>
    public IReadOnlyList<Uri> HomeUrls { get; init; } = [];

    /// <summary>The principal's display name, when the server states one.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The calendars of the home.</summary>
    public required IReadOnlyList<CalDavCalendar> Calendars { get; init; }
}

/// <summary>A calendar of an account (spec 0006 FR-037).</summary>
public sealed record CalDavCalendar {
    /// <summary>The calendar's URL, with a trailing slash.</summary>
    public required Uri Url { get; init; }

    /// <summary>The display name, or <see langword="null"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The color, such as <c>#2E7D32FF</c>, or <see langword="null"/>.</summary>
    public string? Color { get; init; }

    /// <summary>The description, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>The component kinds the calendar holds; events and to-dos when the server does not say.</summary>
    public CalendarComponentKinds Components { get; init; } = CalendarComponentKinds.Event | CalendarComponentKinds.Todo;

    /// <summary>Whether the current user may write objects, from <c>current-user-privilege-set</c>; true when the server does not say.</summary>
    public bool IsWritable { get; init; } = true;

    /// <summary>Whether the server offers <c>sync-collection</c> on the calendar.</summary>
    public bool SupportsSyncCollection { get; init; }
}

/// <summary>An object of a calendar: its name, URL, entity tag and what the reader made of its data.</summary>
/// <param name="Name">The last segment of its URL, such as <c>event-1.ics</c>.</param>
/// <param name="Url">Its URL.</param>
/// <param name="ETag">Its entity tag, or <see langword="null"/> when the server sent none.</param>
/// <param name="Data">The calendar data read by <see cref="CalendarReader"/>, with its located <c>calendar.*</c> errors.</param>
public sealed record CalDavObject(string Name, Uri Url, string? ETag, CalendarReadResult Data);

/// <summary>One sync step (spec 0006 FR-038): what changed and was removed since the previous state, and the new state.</summary>
public sealed record CalDavSyncStep {
    /// <summary>The objects added or changed since the previous state.</summary>
    public required IReadOnlyList<CalDavObject> Changed { get; init; }

    /// <summary>The names of the objects removed since the previous state.</summary>
    public required IReadOnlyList<string> Removed { get; init; }

    /// <summary>The new state: an opaque string the application stores and passes to the next step.</summary>
    public required string State { get; init; }

    /// <summary>Whether more changes are waiting (a step bounded by <see cref="CalDavClientOptions.MaxObjectsPerStep"/>, or a truncated answer); run another step.</summary>
    public bool HasMore { get; init; }
}

/// <summary>What a write did.</summary>
/// <param name="ETag">The new entity tag when the server returned a strong one, else <see langword="null"/> (RFC 4791 §5.3.4); the next sync step fetches it.</param>
public sealed record CalDavWriteOutcome(string? ETag);
