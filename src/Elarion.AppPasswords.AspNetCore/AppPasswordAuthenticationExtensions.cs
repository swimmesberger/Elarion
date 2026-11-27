using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>Adds the app-password authentication scheme (ADR-0111).</summary>
public static class AppPasswordAuthenticationExtensions {
    /// <summary>
    /// Adds the app-password scheme under <see cref="AppPasswordAuthenticationDefaults.AuthenticationScheme"/>. It is not
    /// made the default scheme: name it on the routes calendar apps use (the DAV route group does), so an app password
    /// authenticates nothing else and the browser's credential authenticates nothing there.
    /// </summary>
    /// <remarks>
    /// The host fails to start when the principal cannot be created: set
    /// <see cref="AppPasswordAuthenticationEvents.OnCreatePrincipal"/>, name an
    /// <see cref="AuthenticationSchemeOptions.EventsType"/>, or call
    /// <see cref="AddElarionIdentityAppPasswordPrincipals{TUser}"/>. Credentials are checked through
    /// <see cref="IAppPasswords"/> unless <see cref="AppPasswordAuthenticationEvents.OnVerifyCredential"/> is replaced.
    /// </remarks>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="configure">Configures the options and the events.</param>
    /// <example>
    /// <code>
    /// builder.Services.AddElarionAppPasswordsEntityFrameworkCore&lt;AppDbContext&gt;(
    ///     builder.Configuration.GetSection(AppPasswordOptions.SectionName));
    /// builder.Services.AddElarionIdentityAppPasswordPrincipals&lt;AppUser&gt;();
    /// builder.Services.AddAuthentication().AddElarionAppPasswordAuthentication(o => o.Realm = "Example");
    /// </code>
    /// </example>
    public static AuthenticationBuilder AddElarionAppPasswordAuthentication(
        this AuthenticationBuilder builder,
        Action<AppPasswordAuthenticationOptions>? configure = null) {
        return builder.AddElarionAppPasswordAuthentication(
            AppPasswordAuthenticationDefaults.AuthenticationScheme, configure);
    }

    /// <summary>Adds the app-password scheme under <paramref name="authenticationScheme"/>.</summary>
    /// <param name="builder">The authentication builder.</param>
    /// <param name="authenticationScheme">The scheme name.</param>
    /// <param name="configure">Configures the options and the events.</param>
    public static AuthenticationBuilder AddElarionAppPasswordAuthentication(
        this AuthenticationBuilder builder,
        string authenticationScheme,
        Action<AppPasswordAuthenticationOptions>? configure = null) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationScheme);
        builder.Services.TryAddEnumerable(ServiceDescriptor
            .Singleton<IValidateOptions<AppPasswordAuthenticationOptions>, AppPasswordAuthenticationOptionsValidator>());
        builder.Services.AddOptions<AppPasswordAuthenticationOptions>(authenticationScheme).ValidateOnStart();
        return builder.AddScheme<AppPasswordAuthenticationOptions, AppPasswordAuthenticationHandler>(
            authenticationScheme, AppPasswordAuthenticationDefaults.DisplayName, configure);
    }

    /// <summary>
    /// Creates the principal of an app-password request through ASP.NET Core Identity, when
    /// <see cref="AppPasswordAuthenticationEvents.OnCreatePrincipal"/> is not set: the user is found with
    /// <c>UserManager&lt;TUser&gt;.FindByIdAsync</c>, refused when missing or locked out, and signed in with the claims of
    /// <c>IUserClaimsPrincipalFactory&lt;TUser&gt;</c>, on every request, so a deleted or locked account stops syncing at
    /// once. Requires the Identity services (<c>AddIdentityCore&lt;TUser&gt;()</c> or <c>AddElarionIdentity</c>).
    /// </summary>
    /// <typeparam name="TUser">The Identity user type.</typeparam>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddElarionIdentityAppPasswordPrincipals<TUser>(this IServiceCollection services)
        where TUser : class {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IAppPasswordPrincipalFactory>();
        services.AddSingleton<IAppPasswordPrincipalFactory, IdentityAppPasswordPrincipalFactory<TUser>>();
        return services;
    }
}

/// <summary>Refuses to start a host whose app-password scheme cannot create a principal.</summary>
internal sealed class AppPasswordAuthenticationOptionsValidator(IServiceProviderIsService services)
    : IValidateOptions<AppPasswordAuthenticationOptions> {
    public ValidateOptionsResult Validate(string? name, AppPasswordAuthenticationOptions options) {
        if (options.Events.OnCreatePrincipal is not null
            || options.EventsType is not null
            || services.IsService(typeof(IAppPasswordPrincipalFactory)))
            return ValidateOptionsResult.Success;
        return ValidateOptionsResult.Fail(AppPasswordAuthenticationEvents.MissingPrincipalMessage(name ?? ""));
    }
}
