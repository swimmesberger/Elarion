using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elarion.Typst;

/// <summary>Registers Typst PDF rendering (ADR-0093).</summary>
public static class TypstServiceCollectionExtensions {
    /// <summary>
    /// Registers Typst rendering like <see cref="AddElarionTypst(IServiceCollection, Action{TypstOptions})"/>, binding
    /// <see cref="TypstOptions"/> from <paramref name="configuration"/> first (conventionally the
    /// <see cref="TypstOptions.SectionName"/> section) and applying <paramref name="configure"/> after it. Binding is
    /// source-generated, so it is trimming- and NativeAOT-safe. The options are read once at registration; later
    /// configuration changes do not affect a running host.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration section holding the <see cref="TypstOptions"/> keys.</param>
    /// <param name="configure">Adjusts the bound options, for settings that are code rather than deployment.</param>
    /// <example>
    /// <code>
    /// // appsettings.json: { "Typst": { "Timeout": "00:00:30", "PdfStandards": [ "PdfA2b" ] } }
    /// // environment:      Typst__ExecutablePath=/opt/typst/bin/typst
    /// builder.Services.AddElarionTypst(builder.Configuration.GetSection(TypstOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionTypst(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<TypstOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddElarionTypst(options => {
            configuration.Bind(options);
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Registers <see cref="ITypstCompiler"/>, <see cref="TypstToolchain"/>, the <see cref="TypstOptions"/> instance, and
    /// the startup check (<see cref="TypstOptions.StartupCheck"/>). Safe to call repeatedly - from every module that
    /// renders documents; later <paramref name="configure"/> delegates compose onto the same options instance.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Configures the options; every setting has a working default.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionTypst(options => {
    ///     options.PdfStandards.Add(TypstPdfStandard.PdfA2b);
    ///     options.ExecutablePath = builder.Configuration["Typst:ExecutablePath"];   // optional override
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionTypst(this IServiceCollection services, Action<TypstOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var descriptor = services.LastOrDefault(candidate => candidate.ServiceType == typeof(TypstOptions) && !candidate.IsKeyedService);
        if (descriptor is not null && descriptor.ImplementationInstance is not TypstOptions)
            throw new InvalidOperationException(
                "TypstOptions is already registered with a factory or implementation type. "
                + "Configure Typst via AddElarionTypst(options => …) instead of registering the options yourself.");

        var existing = descriptor?.ImplementationInstance as TypstOptions;
        var options = existing ?? new TypstOptions();
        configure?.Invoke(options);
        options.Validate();
        if (existing is not null) return services;

        services.AddSingleton(options);
        services.TryAddSingleton(static provider => new TypstToolchain(provider.GetRequiredService<TypstOptions>()));
        services.TryAddSingleton<ITypstCompiler, TypstCompiler>();
        // Registered even when the check is None: the decision is read when the host starts, after every configure call.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, TypstStartupCheckService>());
        return services;
    }
}
