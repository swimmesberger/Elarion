using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Elarion.Authorization;

/// <summary>
/// Registers the transport-neutral authorization runtime: the default <see cref="ClaimsAuthorizer"/> and
/// named <see cref="IAuthorizationPolicy"/> instances.
/// </summary>
public static class AuthorizationServiceCollectionExtensions {
    /// <summary>
    /// Registers <see cref="AuthorizationOptions"/> and the default <see cref="IAuthorizer"/>
    /// (<see cref="ClaimsAuthorizer"/>). Required by any host whose handlers use authorization attributes;
    /// <c>AddElarionIdentity</c> calls this for you.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every registration here uses <c>TryAdd</c>, which pins two extension points:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <b>Replacement.</b> A host that registers its own <see cref="IAuthorizer"/> <b>before</b> calling this
    /// method wins — this call then adds nothing for that service.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Decoration.</b> The concrete <see cref="ClaimsAuthorizer"/> is registered as its own service and the
    /// <see cref="IAuthorizer"/> registration resolves it, so a decorating authorizer registered before this call
    /// can inject <see cref="ClaimsAuthorizer"/> as its inner authorizer. No <c>RemoveAll</c>, no
    /// registration-order surgery.
    /// </description>
    /// </item>
    /// </list>
    /// <example>
    /// <code>
    /// // A decorator that adds an audit trail around the shipped decision.
    /// public sealed class AuditingAuthorizer(ClaimsAuthorizer inner, IAuditSink sink) : IAuthorizer {
    ///     public async ValueTask&lt;AppError?&gt; AuthorizeAsync(
    ///         AuthorizationRequirements requirements, object? resource, CancellationToken ct) {
    ///         var error = await inner.AuthorizeAsync(requirements, resource, ct);
    ///         if (error is not null) await sink.RecordDenialAsync(error, ct);
    ///         return error;
    ///     }
    /// }
    ///
    /// builder.Services.AddScoped&lt;IAuthorizer, AuditingAuthorizer&gt;();   // before — TryAdd then defers
    /// builder.Services.AddElarionAuthorization();                        // still registers ClaimsAuthorizer
    /// </code>
    /// </example>
    /// </remarks>
    public static IServiceCollection AddElarionAuthorization(
        this IServiceCollection services,
        Action<AuthorizationOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(services);

        var options = new AuthorizationOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        // The concrete type is a first-class service so a host decorator can take it as its inner authorizer;
        // the interface resolves the same scoped instance rather than constructing a second one.
        services.TryAddScoped<ClaimsAuthorizer>();
        services.TryAddScoped<IAuthorizer>(static sp => sp.GetRequiredService<ClaimsAuthorizer>());
        // Fail-closed default; AddElarionResourceAuthorization replaces it with the grants-backed authorizer.
        services.TryAddScoped<IResourceAuthorizer, DenyResourceAuthorizer>();
        // The generated per-module PermissionCatalogModule contributions (registered via ConfigureDefaultServices)
        // aggregate into this catalog, so seeding/admin code can enumerate every [RequirePermission]/[RequireRole].
        services.TryAddSingleton<IPermissionCatalog, PermissionCatalog>();
        return services;
    }

    /// <summary>
    /// Registers a cross-cutting <see cref="IGlobalAuthorizationRule"/> evaluated for every authorized handler
    /// invocation, after the authenticated gate and before the handler's declared requirements. Additive: call it
    /// once per rule and the rules run in registration order, first denial winning.
    /// </summary>
    /// <remarks>
    /// A rule only runs where the authorization decorator is attached, which the handler-registration generator
    /// does at compile time for handlers carrying a <c>[Require*]</c> attribute or in scope of
    /// <c>[ElarionAuthorizationDefaults]</c>. Pair the rule with an assembly-level
    /// <c>[assembly: ElarionAuthorizationDefaults(RequireAuthenticated = true)]</c> to have it cover every handler.
    /// <c>[AllowAnonymous]</c> handlers skip authorization entirely, rules included.
    /// </remarks>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionAuthorization();
    /// builder.Services.AddElarionGlobalAuthorizationRule&lt;ActiveSubscriptionRule&gt;();
    /// </code>
    /// </example>
    public static IServiceCollection AddElarionGlobalAuthorizationRule<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TRule>(
        this IServiceCollection services)
        where TRule : class, IGlobalAuthorizationRule {
        ArgumentNullException.ThrowIfNull(services);

        // TryAddEnumerable keys on (service, implementation), so registering the same rule twice is a no-op while
        // distinct rules accumulate — the additive-contributor shape used across the framework.
        services.TryAddEnumerable(ServiceDescriptor.Scoped<IGlobalAuthorizationRule, TRule>());
        return services;
    }

    /// <summary>
    /// Registers a named <see cref="IAuthorizationPolicy"/> (resolved from DI, so it may inject services),
    /// bound to <paramref name="name"/>. Usually emitted by the generator from <c>[AuthorizationPolicy("name")]</c>.
    /// </summary>
    public static IServiceCollection AddElarionAuthorizationPolicy<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>(
        this IServiceCollection services, string name)
        where TPolicy : class, IAuthorizationPolicy {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);

        services.TryAddScoped<TPolicy>();
        services.AddScoped(sp => new NamedAuthorizationPolicy(name, sp.GetRequiredService<TPolicy>()));
        return services;
    }

    /// <summary>
    /// Registers a named <see cref="IAuthorizationPolicy"/> whose name is read from its
    /// <see cref="AuthorizationPolicyAttribute"/>. Convenience for manual registration of an attributed policy.
    /// </summary>
    public static IServiceCollection AddElarionAuthorizationPolicy<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TPolicy>(
        this IServiceCollection services)
        where TPolicy : class, IAuthorizationPolicy {
        ArgumentNullException.ThrowIfNull(services);

        var attribute = typeof(TPolicy).GetCustomAttribute<AuthorizationPolicyAttribute>(false)
                        ?? throw new InvalidOperationException(
                            $"'{typeof(TPolicy)}' has no [AuthorizationPolicy] attribute; pass the policy name explicitly.");
        return services.AddElarionAuthorizationPolicy<TPolicy>(attribute.Name);
    }

    /// <summary>Registers a named authorization policy from an inline delegate, for simple checks.</summary>
    public static IServiceCollection AddElarionAuthorizationPolicy(
        this IServiceCollection services,
        string name,
        Func<AuthorizationContext, CancellationToken, ValueTask<bool>> evaluate) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(evaluate);

        services.AddScoped(_ => new NamedAuthorizationPolicy(name, new DelegateAuthorizationPolicy(evaluate)));
        return services;
    }
}
