using System.Globalization;
using Elarion.Abstractions;
using Elarion.Calendars;
using Elarion.WebDav;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

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
        builder.ValidateOnStart();
        DavSources.AddRegistry<CalDavSourceListing, CalDavOptions>(services, CalDavSourceKind.Value);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDavProtocol, CalDavProtocol>());
        services.TryAddScoped<ICalDavPublisher, CalDavPublisher>();
        services.TryAddSingleton<CalDavLinks>();
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
    /// <remarks>
    /// The host refuses to start when no <c>IHandler&lt;TQuery, Result&lt;CalDavSourceListing&gt;&gt;</c> is registered for
    /// the source, so a missing handler is not first noticed by a calendar app.
    /// </remarks>
    public static IServiceCollection AddElarionCalDavSource<TQuery>(this IServiceCollection services) where TQuery : ICalDavSource<TQuery>, new() {
        ArgumentNullException.ThrowIfNull(services);
        DavSources.Register<TQuery, CalDavSourceListing, CalDavOptions>(services, TQuery.SourceName, CalDavSourceKind.Value);
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
        if (int.TryParse(expand[nameof(CalendarExpandOptions.MaxCandidates)], NumberStyles.None, CultureInfo.InvariantCulture, out var candidates))
            options.CalendarExpand.MaxCandidates = candidates;
        if (bool.TryParse(expand[nameof(CalendarExpandOptions.ResolveOverrideVersions)], out var resolveVersions))
            options.CalendarExpand.ResolveOverrideVersions = resolveVersions;
        if (bool.TryParse(expand[nameof(CalendarExpandOptions.SkipCancelled)], out var skipCancelled))
            options.CalendarExpand.SkipCancelled = skipCancelled;
        if (bool.TryParse(configuration[nameof(CalDavOptions.FeedsEnabled)], out var feeds)) options.FeedsEnabled = feeds;
        if (configuration[nameof(CalDavOptions.ProductId)] is { Length: > 0 } productId) options.ProductId = productId;
    }
}
