using System.Text;
using Elarion.Calendars;
using Xunit;

namespace Elarion.Tests.Calendars;

/// <summary>Builds calendar files in the shapes producers write, and reads them.</summary>
internal static class CalendarTestFiles {
    public static async Task<CalendarReadResult> ReadAsync(string text, CalendarReadOptions? options = null) =>
        await ReadAsync(Encoding.UTF8.GetBytes(text), options);

    public static async Task<CalendarReadResult> ReadAsync(byte[] bytes, CalendarReadOptions? options = null) {
        using var stream = new MemoryStream(bytes);
        return await new CalendarReader().ReadAsync(stream, options, TestContext.Current.CancellationToken);
    }

    /// <summary>A calendar of <paramref name="lines"/>, joined by CRLF, between BEGIN and END:VCALENDAR.</summary>
    public static string Calendar(params string[] lines) =>
        string.Join("\r\n", ["BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Test//Test//EN", .. lines, "END:VCALENDAR"]) + "\r\n";

    /// <summary>An event of <paramref name="lines"/>.</summary>
    public static string[] Event(params string[] lines) => ["BEGIN:VEVENT", .. lines, "END:VEVENT"];

    public static async Task<string> WriteAsync(IEnumerable<CalendarItem> items, CalendarHeader? header = null, CalendarWriteOptions? options = null) {
        using var stream = new MemoryStream();
        await CalendarWriter.WriteAsync(stream, items, header, options, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>System time zones, skipping a test when the host has no tzdata for one.</summary>
internal static class TimeZones {
    public static TimeZoneInfo Get(string id) {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone)) Assert.Skip($"The system has no time zone '{id}' (install tzdata).");
        return zone;
    }
}
