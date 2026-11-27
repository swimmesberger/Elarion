using System.Globalization;

namespace Elarion.Contacts;

/// <summary>
/// A birthday or an anniversary (FR-023): a day of a month, with or without a year. A date without a year is what
/// <c>--MMDD</c> (vCard 4.0) and Apple's year 1604 mean.
/// </summary>
public readonly record struct ContactDate {
    /// <summary>Creates a date; the month and the day must form a valid date, in <paramref name="year"/> when one is given.</summary>
    /// <param name="year">The year from 1 to 9999, or <see langword="null"/> for a date without a year.</param>
    /// <param name="month">The month, 1 to 12.</param>
    /// <param name="day">The day of the month; 29 February is valid without a year.</param>
    /// <exception cref="ArgumentOutOfRangeException">The parts do not form a valid date.</exception>
    public ContactDate(int? year, int month, int day) {
        if (!IsValid(year, month, day))
            throw new ArgumentOutOfRangeException(nameof(day), $"{year?.ToString(CultureInfo.InvariantCulture) ?? "--"}-{month}-{day} is not a valid date.");

        Year = year;
        Month = month;
        Day = day;
    }

    /// <summary>The year, or <see langword="null"/> for a date without a year.</summary>
    public int? Year { get; }

    /// <summary>The month, 1 to 12.</summary>
    public int Month { get; }

    /// <summary>The day of the month.</summary>
    public int Day { get; }

    /// <summary>The date with a year as a <see cref="DateOnly"/>; <see langword="null"/> without a year.</summary>
    public DateOnly? ToDateOnly() => Year is { } year ? new DateOnly(year, Month, Day) : null;

    /// <summary>A date with a year.</summary>
    public static ContactDate FromDateOnly(DateOnly date) => new(date.Year, date.Month, date.Day);

    /// <summary>ISO 8601: <c>1980-03-22</c>, or <c>--03-22</c> without a year.</summary>
    public override string ToString() =>
        Year is { } year
            ? string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{Month:D2}-{Day:D2}")
            : string.Create(CultureInfo.InvariantCulture, $"--{Month:D2}-{Day:D2}");

    internal static bool IsValid(int? year, int month, int day) {
        if (month is < 1 or > 12 || day < 1) return false;
        if (year is { } y) return y is >= 1 and <= 9999 && day <= DateTime.DaysInMonth(y, month);

        // Without a year, 29 February is a valid birthday.
        return day <= (month == 2 ? 29 : DateTime.DaysInMonth(2001, month));
    }
}
