namespace Elarion.Abstractions;

/// <summary>
/// Enables all assembly-level Elarion source-generation features for the annotated assembly.
/// </summary>
/// <remarks>
/// This is equivalent to applying <see cref="GenerateModuleHandlersAttribute"/>,
/// <see cref="GenerateModuleServicesAttribute"/>,
/// <see cref="GenerateScheduledJobsAttribute"/>, <see cref="GenerateEventConsumersAttribute"/>,
/// <see cref="GenerateResiliencePoliciesAttribute"/>, <see cref="GenerateActorsAttribute"/>,
/// <see cref="GenerateModuleAuthorizationPoliciesAttribute"/>,
/// <see cref="GenerateClientEventTopicsAttribute"/>, <see cref="GeneratePermissionCatalogAttribute"/>,
/// and <see cref="GenerateVariantCatalogAttribute"/>.
/// Application-owned attributes, such as pipeline defaults, remain explicit because the framework cannot
/// know which policy an application wants.
/// </remarks>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class UseElarionAttribute : Attribute;
