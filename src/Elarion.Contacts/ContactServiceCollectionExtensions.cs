using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Contacts;

/// <summary>Registers the contact reader (ADR-0110).</summary>
public static class ContactServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="ContactReader"/> as a singleton, since it holds no per-request state, and binds
    /// <see cref="ContactOptions"/> from <paramref name="configuration"/>, conventionally the
    /// <see cref="ContactOptions.SectionName"/> section, then applies <paramref name="configure"/>. Binding is
    /// source-generated, so it is trimming- and NativeAOT-safe. Safe to call repeatedly. An unknown
    /// <see cref="ContactReadOptions.FallbackEncoding"/> throws when the reader is resolved.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="ContactOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <example>
    /// <code>
    /// // appsettings.json: { "Contacts": { "ReadDefaults": { "MaxContacts": 20000, "FallbackEncoding": "windows-1250" } } }
    /// builder.Services.AddElarionContacts(builder.Configuration.GetSection(ContactOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionContacts(
        this IServiceCollection services, IConfiguration? configuration = null, Action<ContactOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<ContactOptions>();
        if (configuration is not null) builder.Configure(options => configuration.Bind(options));
        if (configure is not null) builder.Configure(configure);

        services.TryAddSingleton<ContactReader>();
        return services;
    }
}
