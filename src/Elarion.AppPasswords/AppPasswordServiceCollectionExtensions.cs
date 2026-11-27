using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elarion.AppPasswords;

/// <summary>Registers app passwords (ADR-0111).</summary>
public static class AppPasswordServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="IAppPasswords"/> (scoped) and binds <see cref="AppPasswordOptions"/> from
    /// <paramref name="configuration"/>, conventionally the <see cref="AppPasswordOptions.SectionName"/> section, then
    /// applies <paramref name="configure"/>; the options are validated when the host starts. Binding is source-generated,
    /// so it is trimming- and NativeAOT-safe. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// Registers no store: app passwords are durable credentials, so a silent in-memory default would lose them on
    /// restart. Add <c>AddElarionAppPasswordsEntityFrameworkCore&lt;TDbContext&gt;()</c> (production),
    /// <see cref="AddElarionInMemoryAppPasswordStore"/> (tests, development) or an <see cref="IAppPasswordStore"/> of
    /// your own. Resolving <see cref="IAppPasswords"/> without a store throws.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section holding the <see cref="AppPasswordOptions"/> keys, or <see langword="null"/>.</param>
    /// <param name="configure">Adjusts the options after binding.</param>
    /// <example>
    /// <code>
    /// // appsettings.json: { "AppPasswords": { "SecretPrefix": "myapp_", "MaxPerUser": 20 } }
    /// builder.Services.AddElarionAppPasswords(builder.Configuration.GetSection(AppPasswordOptions.SectionName));
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionAppPasswords(
        this IServiceCollection services, IConfiguration? configuration = null,
        Action<AppPasswordOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<AppPasswordOptions>();
        if (configuration is not null) builder.Configure(options => configuration.Bind(options));
        if (configure is not null) builder.Configure(configure);
        builder.ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<AppPasswordOptions>, AppPasswordOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<IAppPasswords>(provider => new AppPasswordService(
            provider.GetService<IAppPasswordStore>() ?? throw new InvalidOperationException(
                "No IAppPasswordStore is registered. Call AddElarionAppPasswordsEntityFrameworkCore<TDbContext>() "
                + "(Elarion.AppPasswords.EntityFrameworkCore), AddElarionInMemoryAppPasswordStore() for tests, or "
                + "register an IAppPasswordStore of your own."),
            provider.GetRequiredService<IOptions<AppPasswordOptions>>(),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>
    /// Registers the <see cref="InMemoryAppPasswordStore"/>, replacing any other store: tests and single-node
    /// development only, because app passwords vanish on restart.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElarionInMemoryAppPasswordStore(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IAppPasswordStore>();
        services.AddSingleton<IAppPasswordStore, InMemoryAppPasswordStore>();
        return services;
    }
}

/// <summary>Refuses out-of-range <see cref="AppPasswordOptions"/> when they are first used or the host starts.</summary>
internal sealed class AppPasswordOptionsValidator : IValidateOptions<AppPasswordOptions> {
    public ValidateOptionsResult Validate(string? name, AppPasswordOptions options) {
        var failures = options.Validate().ToArray();
        return failures.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
