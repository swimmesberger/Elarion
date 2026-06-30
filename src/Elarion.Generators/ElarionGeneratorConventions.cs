using System;

namespace Elarion.Generators;

/// <summary>
/// The conventions that <b>independent generator assemblies must agree on</b> to compose (ADR-0014). Because
/// Roslyn generators cannot observe each other's output, two generators that contribute to one feature
/// coordinate either through referenced-assembly metadata or by sharing this code. This file is
/// <c>&lt;Compile Include&gt;</c>-linked into every generator package, so the agreement is "call the same
/// function," never "copy the same string literal" — which removes the drift hazard between packages.
/// </summary>
internal static class ElarionGeneratorConventions
{
    // --- Marker-attribute metadata names discovered across packages ---------------------------------------

    /// <summary><c>[AppModule]</c> — discovered by the manifest and bootstrapper generators.</summary>
    public const string AppModuleAttribute = "Elarion.Abstractions.Modules.AppModuleAttribute";

    /// <summary>
    /// <c>[ClientFeatures]</c> — declared on an <c>[AppModule]</c> type and read by the manifest generator
    /// (which publishes the exposed names) <b>and</b> the bootstrapper generator (which emits the client-capability
    /// manifest the session handler consumes). See ADR-0020.
    /// </summary>
    public const string ClientFeaturesAttribute = "Elarion.Abstractions.Modules.ClientFeaturesAttribute";

    /// <summary><c>[GenerateDbSets]</c> — required by the EF DbContext, Identity, and resource-grants generators.</summary>
    public const string GenerateDbSetsAttribute = "Elarion.EntityFrameworkCore.GenerateDbSetsAttribute";

    /// <summary>
    /// <c>[ResourceFilter&lt;TEntity&gt;]</c> — discovered by the EF resource-filter generator (which emits the spec)
    /// <b>and</b> the manifest generator (which publishes the descriptor the host bootstrapper registers).
    /// </summary>
    public const string ResourceFilterAttribute = "Elarion.Paging.ResourceFilterAttribute`1";

    // --- EF model-configuration seam naming ---------------------------------------------------------------

    private const string GenerateElarionPrefix = "GenerateElarion";
    private const string AttributeSuffix = "Attribute";

    /// <summary>
    /// Whether an attribute (by short name, e.g. <c>"GenerateElarionResourceGrantsAttribute"</c>) opts a context
    /// into a per-feature model-configuration seam. The EF DbContext generator emits one
    /// <c>partial void OnEntitiesConfigured_{Feature}(ModelBuilder)</c> per such attribute; the owning feature
    /// generator implements the seam derived from its own attribute by <see cref="ModelConfigurationSeamName"/>.
    /// </summary>
    public static bool IsModelConfigurationFeatureAttribute(string? attributeShortName) =>
        attributeShortName is not null
        && attributeShortName.StartsWith(GenerateElarionPrefix, StringComparison.Ordinal)
        && attributeShortName.EndsWith(AttributeSuffix, StringComparison.Ordinal)
        && attributeShortName.Length > AttributeSuffix.Length;

    /// <summary>
    /// The model-configuration seam method name for a <c>[GenerateElarion{Feature}]</c> attribute, e.g.
    /// <c>GenerateElarionResourceGrantsAttribute</c> → <c>OnEntitiesConfigured_GenerateElarionResourceGrants</c>.
    /// The DbContext generator (declaring the seam) and the feature generator (implementing it) both call this,
    /// so they cannot drift.
    /// </summary>
    public static string ModelConfigurationSeamName(string attributeShortName) =>
        "OnEntitiesConfigured_" + StripAttributeSuffix(attributeShortName);

    private static string StripAttributeSuffix(string name) =>
        name.EndsWith(AttributeSuffix, StringComparison.Ordinal)
            ? name.Substring(0, name.Length - AttributeSuffix.Length)
            : name;

    // --- [ResourceFilter] emitted-member contract ---------------------------------------------------------

    /// <summary>The <c>IQueryAuthorizer</c> service type a generated filter spec is registered as (open generic FQN).</summary>
    public const string QueryAuthorizerTypeFqn = "global::Elarion.Abstractions.Authorization.IQueryAuthorizer";

    /// <summary>
    /// The static singleton a field-only filter spec exposes (registered <c>AddSingleton</c>). The resource-filter
    /// generator emits this member; the host bootstrapper registers against it.
    /// </summary>
    public const string ResourceFilterSpecificationMember = "Specification";
}
