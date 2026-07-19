namespace Elarion.Abstractions.Features;

/// <summary>
/// Default <see cref="IVariantCatalog"/> over a fixed descriptor set (the generated registry the host seeds).
/// Key lookup is case-insensitive, matching configuration-key semantics.
/// </summary>
public sealed class DefaultVariantCatalog : IVariantCatalog {
    private readonly Dictionary<string, List<VariantDescriptor>> _byKey;

    /// <summary>Creates the catalog over <paramref name="descriptors"/>.</summary>
    public DefaultVariantCatalog(IEnumerable<VariantDescriptor> descriptors) {
        All = descriptors.ToList();
        _byKey = new Dictionary<string, List<VariantDescriptor>>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in All) {
            if (!_byKey.TryGetValue(descriptor.Key, out var list)) _byKey[descriptor.Key] = list = [];

            list.Add(descriptor);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<VariantDescriptor> All { get; }

    /// <inheritdoc />
    public IReadOnlyList<VariantDescriptor> FindByKey(string key) {
        return _byKey.TryGetValue(key, out var list) ? list : [];
    }
}
