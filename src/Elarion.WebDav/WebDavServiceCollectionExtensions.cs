using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elarion.WebDav;

/// <summary>Registers the WebDAV core (ADR-0111).</summary>
public static class WebDavServiceCollectionExtensions {
    /// <summary>
    /// Registers the <see cref="DavDispatcher"/> and binds <see cref="WebDavOptions"/> from <paramref name="configuration"/>,
    /// conventionally the <see cref="WebDavOptions.SectionName"/> section, then applies <paramref name="configure"/>; the
    /// options are validated when the host starts. Binding is source-generated, so it is trimming- and NativeAOT-safe.
    /// Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// Registers no store: published collections are durable, so a silent in-memory default would lose them on restart.
    /// Add <c>AddElarionWebDavEntityFrameworkCore&lt;TDbContext&gt;()</c> (production) or
    /// <see cref="AddElarionInMemoryDavStore"/> (tests, development). A protocol package (<c>AddElarionCalDav</c>) calls
    /// this method itself.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="WebDavOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <example>
    /// <code>
    /// // appsettings.json: { "WebDav": { "Prefix": "/dav", "JournalRetention": "60.00:00:00" } }
    /// builder.Services.AddElarionWebDav(builder.Configuration.GetSection(WebDavOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionWebDav(this IServiceCollection services, IConfiguration? configuration = null,
        Action<WebDavOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<WebDavOptions>();
        if (configuration is not null) builder.Configure(options => configuration.Bind(options));
        if (configure is not null) builder.Configure(configure);
        builder.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<WebDavOptions>, WebDavOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DavDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="InMemoryDavCollectionStore"/>, replacing any other store: tests and single-node
    /// development only, because the published collections vanish on restart and no transaction is joined.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElarionInMemoryDavStore(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.RemoveAll<IDavCollectionStore>();
        services.AddSingleton<IDavCollectionStore, InMemoryDavCollectionStore>();
        return services;
    }
}

/// <summary>Refuses out-of-range <see cref="WebDavOptions"/> when they are first used or the host starts.</summary>
internal sealed class WebDavOptionsValidator : IValidateOptions<WebDavOptions> {
    public ValidateOptionsResult Validate(string? name, WebDavOptions options) {
        var failures = options.Validate().ToArray();
        return failures.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
