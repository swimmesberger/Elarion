namespace Elarion.Abstractions.Serialization;

/// <summary>
/// A single accumulated contribution to <see cref="ElarionJsonOptions"/>. Registered as an enumerable service by
/// <see cref="ElarionJsonServiceCollectionExtensions.ConfigureElarionJson"/>; the accessor applies every
/// registered configurator, in registration order, when it materializes the canonical options.
/// </summary>
internal sealed class ElarionJsonConfigurator(Action<ElarionJsonOptions> configure) {
    private readonly Action<ElarionJsonOptions> _configure = configure;

    public void Apply(ElarionJsonOptions options) {
        _configure(options);
    }
}
