using Elarion.Abstractions.Features;

namespace Elarion.Features;

/// <summary>
/// The default <see cref="IFeatureFlagCatalog"/>, built once from the registered flag declarations. Two
/// registrations of one name are a wiring error and fail construction — the compiler already rejects a duplicate
/// declaration within the referenced assemblies, so this is the backstop for a host that composes modules the
/// compiler never saw together.
/// </summary>
internal sealed class FeatureFlagCatalog : IFeatureFlagCatalog {
    private readonly Dictionary<string, FeatureFlagRegistration> _byName;

    public FeatureFlagCatalog(IEnumerable<FeatureFlagRegistration> registrations) {
        _byName = new Dictionary<string, FeatureFlagRegistration>(StringComparer.Ordinal);
        foreach (var registration in registrations) {
            if (!_byName.TryAdd(registration.Descriptor.Name, registration)) {
                var existing = _byName[registration.Descriptor.Name].Descriptor;
                throw new InvalidOperationException(
                    $"Feature flag '{registration.Descriptor.Name}' is declared twice (modules '{existing.Module}' and "
                    + $"'{registration.Descriptor.Module}'). A flag has exactly one owner; declare it once.");
            }
        }

        All = _byName.Values
            .Select(static registration => registration.Descriptor)
            .OrderBy(static descriptor => descriptor.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<FeatureFlagDescriptor> All { get; }

    public bool TryGet(string name, out FeatureFlagDescriptor descriptor) {
        if (_byName.TryGetValue(name, out var registration)) {
            descriptor = registration.Descriptor;
            return true;
        }

        descriptor = null!;
        return false;
    }

    /// <summary>Looks up the registration (descriptor plus owner dispatch) for <paramref name="name"/>.</summary>
    public bool TryGetRegistration(string name, out FeatureFlagRegistration registration) {
        return _byName.TryGetValue(name, out registration!);
    }
}
