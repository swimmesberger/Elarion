using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Spreadsheets;

/// <summary>Registers the spreadsheet reader (ADR-0108).</summary>
public static class SpreadsheetServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="SpreadsheetReader"/> (scoped, because it validates rows through the scoped request
    /// validator) and binds <see cref="SpreadsheetOptions"/> from <paramref name="configuration"/>, conventionally the
    /// <see cref="SpreadsheetOptions.SectionName"/> section, then applies <paramref name="configure"/>. Binding is
    /// source-generated, so it is trimming- and NativeAOT-safe. Safe to call repeatedly.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="SpreadsheetOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <example>
    /// <code>
    /// // appsettings.json: { "Spreadsheets": { "ReadDefaults": { "MaxRows": 20000, "TrueWords": [ "ja", "j" ] } } }
    /// builder.Services.AddElarionSpreadsheets(builder.Configuration.GetSection(SpreadsheetOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionSpreadsheets(
        this IServiceCollection services, IConfiguration? configuration = null, Action<SpreadsheetOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<SpreadsheetOptions>();
        if (configuration is not null) builder.Configure(options => configuration.Bind(options));
        if (configure is not null) builder.Configure(configure);

        services.TryAddScoped<SpreadsheetReader>();
        return services;
    }
}
