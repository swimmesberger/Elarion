namespace Elarion.Session;

/// <summary>
/// Adds one named section to the client-capability snapshot (<see cref="SessionResponse.Sections"/>). The
/// framework owns the snapshot's fixed shape — user, modules, flags, variants — and this is the seam for the
/// application-specific bootstrap data a frontend would otherwise fetch with a second round trip: the current
/// tenant, branding, a server clock, an onboarding state.
/// </summary>
/// <remarks>
/// <para>
/// Register with <c>AddElarionClientSnapshotContributor&lt;T&gt;()</c>. Contributors are resolved per request, so
/// a contributor may inject scoped services (the current user, a <c>DbContext</c>). Returning
/// <see langword="null"/> omits the section entirely rather than emitting a null value, so a section can be
/// conditional on the caller without the frontend having to distinguish "absent" from "null".
/// </para>
/// <para>
/// <b>A section is a read-only UX projection, never an enforcement surface.</b> It carries exactly what the
/// application chose to name and nothing more — the same leak-safety rule the rest of the snapshot follows. The
/// real gate on every operation is still the handler's <c>[RequirePermission]</c>/<c>[FeatureGate]</c>.
/// </para>
/// <para>
/// The payload is serialized polymorphically from its runtime type, so that type <b>must</b> be reachable through
/// a source-generated <c>JsonSerializerContext</c> — pass the contributor's own context to the registration
/// (<c>AddElarionClientSnapshotContributor&lt;T&gt;(MyContext.Default)</c>) and the snapshot stays AOT-safe with
/// no reflection fallback.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed record TenantSection {
///     public required string Name { get; init; }
///     public required string Theme { get; init; }
/// }
///
/// [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
/// [JsonSerializable(typeof(TenantSection))]
/// public sealed partial class TenantSectionJsonContext : JsonSerializerContext;
///
/// public sealed class TenantSectionContributor(ITenantContext tenants) : IClientSnapshotContributor {
///     public string SectionName => "tenant";
///
///     public async ValueTask&lt;object?&gt; GetSectionAsync(CancellationToken cancellationToken) {
///         var tenant = await tenants.GetCurrentAsync(cancellationToken);
///         return tenant is null ? null : new TenantSection { Name = tenant.Name, Theme = tenant.Theme };
///     }
/// }
///
/// // Program.cs
/// builder.Services.AddElarionClientSnapshotContributor&lt;TenantSectionContributor&gt;(
///     TenantSectionJsonContext.Default);
/// </code>
/// </example>
public interface IClientSnapshotContributor {
    /// <summary>
    /// The wire key this contributor writes under <c>sections</c>. Compared ordinally and must be unique across
    /// registered contributors — section names are dictionary keys the frontend reads by name, so a collision is
    /// a wiring bug that fails loudly rather than letting one contributor silently shadow another.
    /// </summary>
    string SectionName { get; }

    /// <summary>
    /// Produces the section payload for the current request, or <see langword="null"/> to omit the section.
    /// </summary>
    ValueTask<object?> GetSectionAsync(CancellationToken cancellationToken);
}
