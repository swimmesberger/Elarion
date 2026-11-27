using System.Globalization;
using Elarion.Abstractions;
using Elarion.Calendars;
using Elarion.WebDav;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.CalDav;

/// <summary>Registers CalDAV (ADR-0111).</summary>
public static class CalDavServiceCollectionExtensions {
    /// <summary>
    /// Registers CalDAV on the WebDAV core (<see cref="WebDavServiceCollectionExtensions.AddElarionWebDav"/>): the
    /// calendar protocol, <see cref="ICalDavPublisher"/> (scoped) and <see cref="CalendarReader"/>, and binds
    /// <see cref="CalDavOptions"/> from <paramref name="configuration"/>, conventionally the
    /// <see cref="CalDavOptions.SectionName"/> section, then applies <paramref name="configure"/>. Binding is
    /// source-generated, so it is trimming- and NativeAOT-safe. Safe to call repeatedly. A store comes from
    /// <c>AddElarionWebDavEntityFrameworkCore&lt;TDbContext&gt;()</c>; calendars from <see cref="AddElarionCalDavSource{TQuery}"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="CalDavOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <remarks><c>CalendarExpand:FloatingTimeZone</c> is not read: floating times are placed in each calendar's zone.</remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionWebDavEntityFrameworkCore&lt;AppDbContext&gt;(builder.Configuration.GetSection("WebDav"));
    /// builder.Services.AddElarionCalDav(builder.Configuration.GetSection(CalDavOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionCalDav(this IServiceCollection services, IConfiguration? configuration = null,
        Action<CalDavOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionWebDav();
        services.AddElarionCalendars();
        var builder = services.AddOptions<CalDavOptions>();
        if (configuration is not null) builder.Configure(options => Bind(configuration, options));
        if (configure is not null) builder.Configure(configure);

        services.TryAddSingleton<CalDavSourceRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDavProtocol, CalDavProtocol>());
        services.TryAddScoped<ICalDavPublisher, CalDavPublisher>();
        return services;
    }

    /// <summary>
    /// Registers the calendar source <typeparamref name="TQuery"/> (spec 0006 FR-010): every DAV request invokes its
    /// handler through the handler pipeline as the authenticated user and serves only the calendars it returns. Call it
    /// in the module's <c>ConfigureServices</c>, so a disabled module registers no source.
    /// </summary>
    /// <typeparam name="TQuery">The source's query request.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <exception cref="InvalidOperationException">The source name is invalid, or another source has the same name (FR-013).</exception>
    public static IServiceCollection AddElarionCalDavSource<TQuery>(this IServiceCollection services) where TQuery : ICalDavSource<TQuery>, new() {
        ArgumentNullException.ThrowIfNull(services);
        var name = TQuery.SourceName;
        if (!CalDavAddress.IsValidSegment(name))
            throw new InvalidOperationException(
                $"The calendar source {typeof(TQuery).Name} is named '{name}'; a source name must be 1 to 64 ASCII letters, digits, '-' or '_'.");
        foreach (var descriptor in services)
            if (descriptor.ServiceType == typeof(CalDavSourceRegistration) && descriptor.ImplementationInstance is CalDavSourceRegistration existing
                                                                           && existing.Name == name)
                throw new InvalidOperationException($"Two calendar sources are named '{name}'; {typeof(TQuery).Name} must use another name.");

        services.AddSingleton(new CalDavSourceRegistration(name, static (provider, cancellationToken) =>
            provider.GetRequiredService<IHandler<TQuery, Result<CalDavSourceListing>>>().HandleAsync(new TQuery(), cancellationToken)));
        return services;
    }

    // CalendarRead binds through the generator; CalendarExpand by hand, as AddElarionCalendars does.
    private static void Bind(IConfiguration configuration, CalDavOptions options) {
        configuration.GetSection(nameof(CalDavOptions.CalendarRead)).Bind(options.CalendarRead);
        var expand = configuration.GetSection(nameof(CalDavOptions.CalendarExpand));
        if (int.TryParse(expand[nameof(CalendarExpandOptions.MaxOccurrences)], NumberStyles.None, CultureInfo.InvariantCulture, out var occurrences))
            options.CalendarExpand.MaxOccurrences = occurrences;
        if (int.TryParse(expand[nameof(CalendarExpandOptions.MaxIterations)], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations))
            options.CalendarExpand.MaxIterations = iterations;
        if (bool.TryParse(configuration[nameof(CalDavOptions.FeedsEnabled)], out var feeds)) options.FeedsEnabled = feeds;
        if (configuration[nameof(CalDavOptions.ProductId)] is { Length: > 0 } productId) options.ProductId = productId;
    }
}
