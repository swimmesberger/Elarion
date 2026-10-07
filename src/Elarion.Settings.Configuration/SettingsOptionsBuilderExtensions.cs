using System.Text.Json;
using Elarion.Abstractions.Serialization;
using Elarion.Abstractions.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Elarion.Settings.Configuration;

/// <summary>Binds secret settings onto options, the one place a secret meets options without passing through <c>IConfiguration</c>.</summary>
public static class SettingsOptionsBuilderExtensions {
    /// <summary>
    /// Applies the stored value of the secret <paramref name="definition"/> to <typeparamref name="TOptions"/> whenever
    /// the options are built, and reloads <c>IOptionsMonitor</c>/<c>IOptionsSnapshot</c> when it changes. Secrets are
    /// never projected into <c>IConfiguration</c> (it is routinely bound, logged and dumped), so an options type bound
    /// from configuration cannot otherwise receive one; this keeps the rest of the section bound from configuration and
    /// sets only the secret member.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value comes from the resolution <c>AddElarionSettingsConfiguration</c>'s refresher already performs, so that
    /// call is required; only the definitions bound here are kept in memory. <paramref name="apply"/> is not called
    /// while the secret is unset or unreadable, so the member keeps its configured or default value.
    /// </para>
    /// <para>
    /// The refresher loads at host start, so read the options through <c>IOptionsMonitor&lt;T&gt;</c> or
    /// <c>IOptionsSnapshot&lt;T&gt;</c>: an <c>IOptions&lt;T&gt;</c> value built before the host started never sees the
    /// secret, and never sees a later change.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.AddElarionSettingsConfiguration();
    /// builder.Services.AddOptions&lt;SmtpOptions&gt;()
    ///     .BindConfiguration("Email:Smtp")                                   // host, port, user from configuration
    ///     .BindSecretSetting(AppSettings.SmtpPassword, static (o, password) =&gt; o.Password = password);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="definition"/> is not a secret; non-secret settings are
    /// already projected into <c>IConfiguration</c> and bind from there.</exception>
    public static OptionsBuilder<TOptions> BindSecretSetting<TOptions, T>(
        this OptionsBuilder<TOptions> builder, SettingDefinition<T> definition, Action<TOptions, T> apply)
        where TOptions : class {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(apply);
        if (!definition.IsSecret)
            throw new ArgumentException(
                $"Setting '{definition.Key}' is not a secret. Non-secret settings are projected into IConfiguration; " +
                "bind them from configuration instead.", nameof(definition));

        var values = GetOrAddValues(builder.Services);
        values.Track(definition.Key);

        builder.Configure<IElarionJsonSerialization>((options, serialization) => {
            if (values.GetJson(definition.Key) is not { } json) return;

            var value = JsonSerializer.Deserialize(json, serialization.GetTypeInfo<T>());
            if (value is not null) apply(options, value);
        });
        builder.Services.AddSingleton<IOptionsChangeTokenSource<TOptions>>(
            new SecretChangeTokenSource<TOptions>(builder.Name, values));

        return builder;
    }

    // One instance per container, created at registration so every binding tracks its key on the same object.
    private static SettingsSecretValues GetOrAddValues(IServiceCollection services) {
        if (services.FirstOrDefault(static d => d.ServiceType == typeof(SettingsSecretValues))
                ?.ImplementationInstance is SettingsSecretValues existing)
            return existing;

        var values = new SettingsSecretValues();
        services.AddSingleton(values);
        return values;
    }

    private sealed class SecretChangeTokenSource<TOptions>(string? name, SettingsSecretValues values)
        : IOptionsChangeTokenSource<TOptions> {
        public string Name { get; } = name ?? Options.DefaultName;

        public IChangeToken GetChangeToken() {
            return values.GetChangeToken();
        }
    }
}
