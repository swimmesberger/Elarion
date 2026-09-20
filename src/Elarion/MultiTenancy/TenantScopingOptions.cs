namespace Elarion.MultiTenancy;

/// <summary>
/// Configuration for the shipped claim-based tenant resolution. An application that resolves tenancy another
/// way replaces <c>ITenantResolver</c> instead and leaves these alone.
/// </summary>
public sealed class TenantScopingOptions {
    /// <summary>
    /// The claim type the tenant id is read from. Defaults to <c>"tenant"</c>, matching the default
    /// <c>[ResourceFilter(TenantClaimType = …)]</c> uses, so the two legs agree without configuration.
    /// </summary>
    public string ClaimType { get; set; } = "tenant";
}
