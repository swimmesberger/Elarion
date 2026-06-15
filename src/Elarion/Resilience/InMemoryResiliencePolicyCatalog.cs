using System.Collections.Concurrent;
using Elarion.Abstractions.Resilience;

namespace Elarion.Resilience;

internal sealed class InMemoryResiliencePolicyCatalog(
    IEnumerable<ResiliencePolicyMetadataRegistration> registrations
) : IResiliencePolicyCatalog {
    private readonly ConcurrentDictionary<string, ResiliencePolicyMetadata> _policies = new(StringComparer.Ordinal);

    public ResiliencePolicyMetadata? GetPolicy(ResiliencePolicyReference policy) {
        EnsureInitialized();
        return _policies.TryGetValue(policy.Name, out var metadata) ? metadata : null;
    }

    private void EnsureInitialized() {
        if (!_policies.IsEmpty) {
            return;
        }

        foreach (var registration in registrations) {
            _policies[registration.Metadata.Name] = registration.Metadata;
        }
    }
}
