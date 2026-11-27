using System.Diagnostics.CodeAnalysis;

namespace Elarion.AppPasswords;

/// <summary>What an app password may be used for (ADR-0111).</summary>
public enum AppPasswordScopeKind {
    /// <summary>A scope this version does not know; it never authenticates through the app-password scheme.</summary>
    Other = 0,

    /// <summary><c>dav</c>: a calendar app's account, sent over HTTP Basic.</summary>
    Dav = 1,

    /// <summary><c>calendar-feed:{source}.{key}</c>: a feed token, the read-only ICS feed of one published calendar.</summary>
    CalendarFeed = 2,
}

/// <summary>
/// The scope of an app password: <see cref="Dav"/> for a calendar app's account, or
/// <see cref="CalendarFeed(string, string)"/> for a feed token bound to one published calendar. The scope is stored as
/// its <see cref="Value"/>, which is also the value of the <see cref="AppPasswordClaimTypes.Scope"/> claim.
/// </summary>
/// <remarks>
/// A feed scope addresses a calendar by its calendar source name and calendar key, each 1 to 64 characters of ASCII
/// letters, digits, <c>-</c> and <c>_</c> (spec 0006, FR-013), as <c>calendar-feed:{source}.{key}</c>. Two scopes are
/// equal when their values are equal, ordinally.
/// </remarks>
/// <example>
/// <code>
/// await appPasswords.CreateAsync(userId, "iPhone", AppPasswordScope.Dav, ct);
/// await appPasswords.CreateAsync(userId, "Outlook", AppPasswordScope.CalendarFeed("shifts", "u-42"), ct);
/// </code>
/// </example>
public sealed class AppPasswordScope : IEquatable<AppPasswordScope> {
    /// <summary>The value of the <see cref="Dav"/> scope: <c>dav</c>.</summary>
    public const string DavValue = "dav";

    /// <summary>The prefix of a feed scope's value: <c>calendar-feed:</c>.</summary>
    public const string CalendarFeedPrefix = "calendar-feed:";

    /// <summary>The longest scope value accepted: 200 characters.</summary>
    public const int MaxLength = 200;

    /// <summary>The longest calendar source name or calendar key of a feed scope: 64 characters.</summary>
    public const int MaxSegmentLength = 64;

    private AppPasswordScope(string value, AppPasswordScopeKind kind, string? source, string? key) {
        Value = value;
        Kind = kind;
        CalendarSource = source;
        CalendarKey = key;
    }

    /// <summary>The scope of a calendar app's account, sent over HTTP Basic.</summary>
    public static AppPasswordScope Dav { get; } = new(DavValue, AppPasswordScopeKind.Dav, null, null);

    /// <summary>The scope as stored and as claimed, for example <c>dav</c> or <c>calendar-feed:shifts.u-42</c>.</summary>
    public string Value { get; }

    /// <summary>Which kind of scope this is.</summary>
    public AppPasswordScopeKind Kind { get; }

    /// <summary>The calendar source name of a feed scope; <see langword="null"/> for any other kind.</summary>
    public string? CalendarSource { get; }

    /// <summary>The calendar key of a feed scope; <see langword="null"/> for any other kind.</summary>
    public string? CalendarKey { get; }

    /// <summary>The scope of a feed token for the published calendar <paramref name="key"/> of <paramref name="source"/>.</summary>
    /// <param name="source">The calendar source name: 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</param>
    /// <param name="key">The calendar key within the source: 1 to 64 ASCII letters, digits, <c>-</c> or <c>_</c>.</param>
    /// <exception cref="ArgumentException">A segment is empty, too long, or holds another character.</exception>
    public static AppPasswordScope CalendarFeed(string source, string key) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(key);
        if (!IsSegment(source))
            throw new ArgumentException(
                $"A calendar source name must be 1 to {MaxSegmentLength} ASCII letters, digits, '-' or '_'.", nameof(source));
        if (!IsSegment(key))
            throw new ArgumentException(
                $"A calendar key must be 1 to {MaxSegmentLength} ASCII letters, digits, '-' or '_'.", nameof(key));
        return new AppPasswordScope(CalendarFeedPrefix + source + "." + key, AppPasswordScopeKind.CalendarFeed, source, key);
    }

    /// <summary>
    /// Reads a stored or claimed scope value. <c>dav</c> and well-formed feed scopes get their kind; any other value of
    /// lowercase letters, digits and <c>-</c>, optionally followed by <c>:</c> and printable ASCII, is kept as
    /// <see cref="AppPasswordScopeKind.Other"/>, so a row written by a later version still reads.
    /// </summary>
    /// <exception cref="FormatException">The value is empty, longer than <see cref="MaxLength"/>, or malformed.</exception>
    public static AppPasswordScope Parse(string value) {
        ArgumentNullException.ThrowIfNull(value);
        return TryParse(value, out var scope)
            ? scope
            : throw new FormatException($"'{value}' is not an app password scope.");
    }

    /// <summary>Reads a scope value as <see cref="Parse"/> does, without throwing.</summary>
    public static bool TryParse([NotNullWhen(true)] string? value, [NotNullWhen(true)] out AppPasswordScope? scope) {
        scope = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength) return false;
        if (string.Equals(value, DavValue, StringComparison.Ordinal)) {
            scope = Dav;
            return true;
        }

        if (value.StartsWith(CalendarFeedPrefix, StringComparison.Ordinal)) {
            var rest = value.AsSpan(CalendarFeedPrefix.Length);
            var dot = rest.IndexOf('.');
            if (dot < 0) return false;
            var source = rest[..dot].ToString();
            var key = rest[(dot + 1)..].ToString();
            if (!IsSegment(source) || !IsSegment(key)) return false;
            scope = new AppPasswordScope(value, AppPasswordScopeKind.CalendarFeed, source, key);
            return true;
        }

        var colon = value.IndexOf(':');
        var name = colon < 0 ? value.AsSpan() : value.AsSpan(0, colon);
        if (name.IsEmpty) return false;
        foreach (var c in name)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
                return false;
        if (colon >= 0)
            foreach (var c in value.AsSpan(colon + 1))
                if (c is < '!' or > '~')
                    return false;
        scope = new AppPasswordScope(value, AppPasswordScopeKind.Other, null, null);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(AppPasswordScope? other) {
        return other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) {
        return obj is AppPasswordScope other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode() {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <summary>Returns <see cref="Value"/>.</summary>
    public override string ToString() {
        return Value;
    }

    /// <summary>Whether two scopes have the same value.</summary>
    public static bool operator ==(AppPasswordScope? left, AppPasswordScope? right) {
        return left is null ? right is null : left.Equals(right);
    }

    /// <summary>Whether two scopes have different values.</summary>
    public static bool operator !=(AppPasswordScope? left, AppPasswordScope? right) {
        return !(left == right);
    }

    private static bool IsSegment(ReadOnlySpan<char> segment) {
        if (segment.IsEmpty || segment.Length > MaxSegmentLength) return false;
        foreach (var c in segment)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        return true;
    }
}
