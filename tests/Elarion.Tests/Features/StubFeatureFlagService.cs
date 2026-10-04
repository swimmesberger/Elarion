using Elarion.Abstractions.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Tests.Features;

/// <summary>
/// A scripted <see cref="IFeatureFlagService"/> for decorator and session tests: fixed enablement and variants per
/// flag, with every queried name and every evaluation context recorded.
/// </summary>
internal sealed class StubFeatureFlagService : IFeatureFlagService {
    private static readonly IServiceProvider EmptyServices = new ServiceCollection().BuildServiceProvider();

    public StubFeatureFlagService(params (string Name, bool Enabled)[] flags) {
        foreach (var (name, enabled) in flags) Flags[name] = enabled;
    }

    public Dictionary<string, bool> Flags { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> Variants { get; } = new(StringComparer.Ordinal);

    public List<string> Queried { get; } = [];

    public List<FeatureEvaluationContext> Contexts { get; } = [];

    public FeatureEvaluationContext CreateContext() {
        return new FeatureEvaluationContext { Services = EmptyServices };
    }

    public ValueTask<bool> IsEnabledAsync(string flag, CancellationToken ct = default) {
        return IsEnabledAsync(flag, CreateContext(), ct);
    }

    public ValueTask<bool> IsEnabledAsync(string flag, FeatureEvaluationContext context, CancellationToken ct = default) {
        Queried.Add(flag);
        Contexts.Add(context);
        return ValueTask.FromResult(Flags.TryGetValue(flag, out var enabled) && enabled);
    }

    public ValueTask<string?> GetVariantAsync(string flag, CancellationToken ct = default) {
        return GetVariantAsync(flag, CreateContext(), ct);
    }

    public ValueTask<string?> GetVariantAsync(string flag, FeatureEvaluationContext context, CancellationToken ct = default) {
        return ValueTask.FromResult(Variants.TryGetValue(flag, out var variant) ? variant : null);
    }
}

/// <summary>A fixed <see cref="IFeatureFlagCatalog"/> built from descriptors.</summary>
internal sealed class StubFeatureFlagCatalog(params FeatureFlagDescriptor[] flags) : IFeatureFlagCatalog {
    public IReadOnlyList<FeatureFlagDescriptor> All { get; } = flags.OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();

    public bool TryGet(string name, out FeatureFlagDescriptor descriptor) {
        descriptor = All.FirstOrDefault(f => f.Name == name)!;
        return descriptor is not null;
    }

    public static FeatureFlagDescriptor Flag(string name, string module, bool expose = true) {
        return new FeatureFlagDescriptor {
            Name = name, Module = module, ExposeToClient = expose, Owner = FeatureFlagOwner.Code
        };
    }
}
