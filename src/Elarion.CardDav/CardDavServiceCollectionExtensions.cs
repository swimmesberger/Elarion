using Elarion.Contacts;
using Elarion.WebDav;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.CardDav;

/// <summary>Registers CardDAV (ADR-0113).</summary>
public static class CardDavServiceCollectionExtensions {
    /// <summary>
    /// Registers CardDAV on the WebDAV core (<see cref="WebDavServiceCollectionExtensions.AddElarionWebDav"/>): the
    /// address-book protocol, <see cref="ICardDavPublisher"/> (scoped) and <see cref="ContactReader"/>, and binds
    /// <see cref="CardDavOptions"/> from <paramref name="configuration"/>, conventionally the
    /// <see cref="CardDavOptions.SectionName"/> section, then applies <paramref name="configure"/>. Binding is
    /// source-generated, so it is trimming- and NativeAOT-safe. Safe to call repeatedly. A store comes from
    /// <c>AddElarionWebDavEntityFrameworkCore&lt;TDbContext&gt;()</c>; address books from
    /// <see cref="AddElarionCardDavSource{TQuery}"/>. The host's <c>MapElarionDav()</c> serves them beside any calendars.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="CardDavOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionWebDavEntityFrameworkCore&lt;AppDbContext&gt;(builder.Configuration.GetSection("WebDav"));
    /// builder.Services.AddElarionCardDav(builder.Configuration.GetSection(CardDavOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionCardDav(this IServiceCollection services, IConfiguration? configuration = null,
        Action<CardDavOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        services.AddElarionWebDav();
        services.AddElarionContacts();
        var builder = services.AddOptions<CardDavOptions>();
        if (configuration is not null) builder.Configure(options => configuration.GetSection(nameof(CardDavOptions.ContactRead)).Bind(options.ContactRead));
        if (configure is not null) builder.Configure(configure);
        builder.ValidateOnStart();
        DavSources.AddRegistry<CardDavSourceListing, CardDavOptions>(services, CardDavSourceKind.Value);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDavProtocol, CardDavProtocol>());
        services.TryAddScoped<ICardDavPublisher, CardDavPublisher>();
        return services;
    }

    /// <summary>
    /// Registers the address-book source <typeparamref name="TQuery"/> (spec 0007 FR-011): every DAV request invokes its
    /// handler through the handler pipeline as the authenticated user and serves only the address books it returns. Call
    /// it in the module's <c>ConfigureServices</c>, so a disabled module registers no source.
    /// </summary>
    /// <typeparam name="TQuery">The source's query request.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <exception cref="InvalidOperationException">The source name is invalid, or another address-book source has the same name (FR-014).</exception>
    /// <remarks>
    /// The host refuses to start when no <c>IHandler&lt;TQuery, Result&lt;CardDavSourceListing&gt;&gt;</c> is registered
    /// for the source, so a missing handler is not first noticed by a contacts app.
    /// </remarks>
    public static IServiceCollection AddElarionCardDavSource<TQuery>(this IServiceCollection services) where TQuery : ICardDavSource<TQuery>, new() {
        ArgumentNullException.ThrowIfNull(services);
        DavSources.Register<TQuery, CardDavSourceListing, CardDavOptions>(services, TQuery.SourceName, CardDavSourceKind.Value);
        return services;
    }
}
