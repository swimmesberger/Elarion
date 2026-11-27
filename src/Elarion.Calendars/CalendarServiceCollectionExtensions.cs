using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Calendars;

/// <summary>Registers the calendar reader (ADR-0109).</summary>
public static class CalendarServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="CalendarReader"/> as a singleton and binds <see cref="CalendarOptions"/> from
    /// <paramref name="configuration"/>, conventionally the <see cref="CalendarOptions.SectionName"/> section, then
    /// applies <paramref name="configure"/>. Binding is source-generated, so it is trimming- and NativeAOT-safe. Safe to
    /// call repeatedly.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="CalendarOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <remarks><c>ExpandDefaults:FloatingTimeZone</c> is a time-zone id, such as <c>Europe/Vienna</c>.</remarks>
    /// <example>
    /// <code>
    /// // appsettings.json: { "Calendars": { "ReadDefaults": { "MaxBytes": 4194304 } } }
    /// builder.Services.AddElarionCalendars(builder.Configuration.GetSection(CalendarOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionCalendars(
        this IServiceCollection services, IConfiguration? configuration = null, Action<CalendarOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<CalendarOptions>();
        if (configuration is not null) builder.Configure(options => Bind(configuration, options));
        if (configure is not null) builder.Configure(configure);

        services.TryAddSingleton<CalendarReader>();
        return services;
    }

    // ReadDefaults binds through the generator; ExpandDefaults by hand, because a TimeZoneInfo is configured by its id.
    private static void Bind(IConfiguration configuration, CalendarOptions options) {
        configuration.GetSection(nameof(CalendarOptions.ReadDefaults)).Bind(options.ReadDefaults);
        var expand = configuration.GetSection(nameof(CalendarOptions.ExpandDefaults));
        if (int.TryParse(expand[nameof(CalendarExpandOptions.MaxOccurrences)], NumberStyles.None, CultureInfo.InvariantCulture, out var occurrences))
            options.ExpandDefaults.MaxOccurrences = occurrences;
        if (int.TryParse(expand[nameof(CalendarExpandOptions.MaxIterations)], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            options.ExpandDefaults.MaxIterations = iterations;
        if (expand[nameof(CalendarExpandOptions.FloatingTimeZone)] is { Length: > 0 } zoneId) {
            options.ExpandDefaults.FloatingTimeZone = CalendarTimeZones.TryResolve(zoneId, out var zone)
                ? zone
                : throw new InvalidOperationException($"The floating time zone '{zoneId}' is not known to the system.");
        }
    }
}
