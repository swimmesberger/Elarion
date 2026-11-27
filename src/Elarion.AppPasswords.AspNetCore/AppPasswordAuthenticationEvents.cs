using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>
/// What happens between the credential and the principal, in order: <see cref="OnVerifyCredential"/>,
/// <see cref="OnValidateUserName"/> (HTTP Basic only), <see cref="OnCreatePrincipal"/> and
/// <see cref="OnAuthenticated"/> (ADR-0111). Set the delegates where the scheme is added, or subclass this type,
/// override the virtual methods, register the subclass and name it in
/// <see cref="Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions.EventsType"/>, as with <c>JwtBearerEvents</c>.
/// </summary>
/// <example>
/// <code>
/// builder.Services.AddAuthentication().AddElarionAppPasswordAuthentication(o => {
///     o.Events.OnCreatePrincipal = async ctx => {
///         var accounts = ctx.Services.GetRequiredService&lt;AccountService&gt;();
///         return await accounts.CreatePrincipalAsync(ctx.Match.UserId, ctx.CancellationToken);   // null refuses
///     };
/// });
/// </code>
/// </example>
public class AppPasswordAuthenticationEvents {
    /// <summary>
    /// Checks the secret and returns its match, or <see langword="null"/> to refuse. The default checks Elarion's
    /// store through <see cref="IAppPasswords"/> for scope <c>dav</c> over HTTP Basic, or any calendar-feed scope for a
    /// feed token. An application with credentials of its own sets this and needs no store:
    /// <see cref="IAppPasswords"/> and <see cref="IAppPasswordStore"/> are then never resolved. Whatever it returns,
    /// the scheme refuses a match whose scope kind is not <see cref="AppPasswordCredentialContext.RequiredKind"/>.
    /// </summary>
    public Func<AppPasswordCredentialContext, ValueTask<AppPasswordMatch?>> OnVerifyCredential { get; set; } =
        VerifyWithAppPasswordsAsync;

    /// <summary>
    /// Checks the HTTP Basic user name against the match; <see langword="false"/> refuses. Default: not checked, since
    /// the secret identifies the credential. Not run for feed tokens, which carry no user name.
    /// </summary>
    public Func<AppPasswordUserNameContext, ValueTask<bool>> OnValidateUserName { get; set; } =
        static _ => ValueTask.FromResult(true);

    /// <summary>
    /// Creates the principal of <see cref="AppPasswordMatch.UserId"/> on every request; <see langword="null"/> refuses
    /// (a deactivated or deleted account). No default: when it is not set, the registered
    /// <see cref="IAppPasswordPrincipalFactory"/> is used, which
    /// <c>AddElarionIdentityAppPasswordPrincipals&lt;TUser&gt;()</c> registers for ASP.NET Core Identity; with neither,
    /// the host fails to start.
    /// </summary>
    public Func<AppPasswordPrincipalContext, ValueTask<ClaimsPrincipal?>>? OnCreatePrincipal { get; set; }

    /// <summary>Observes a successful authentication, for audit and metrics. Default: nothing.</summary>
    public Func<AppPasswordAuthenticatedContext, ValueTask> OnAuthenticated { get; set; } =
        static _ => ValueTask.CompletedTask;

    /// <summary>Runs <see cref="OnVerifyCredential"/>.</summary>
    public virtual ValueTask<AppPasswordMatch?> VerifyCredential(AppPasswordCredentialContext context) {
        return OnVerifyCredential(context);
    }

    /// <summary>Runs <see cref="OnValidateUserName"/>.</summary>
    public virtual ValueTask<bool> ValidateUserName(AppPasswordUserNameContext context) {
        return OnValidateUserName(context);
    }

    /// <summary>Runs <see cref="OnCreatePrincipal"/>, or the registered <see cref="IAppPasswordPrincipalFactory"/>.</summary>
    /// <exception cref="InvalidOperationException">Neither is there.</exception>
    public virtual ValueTask<ClaimsPrincipal?> CreatePrincipal(AppPasswordPrincipalContext context) {
        ArgumentNullException.ThrowIfNull(context);
        if (OnCreatePrincipal is not null) return OnCreatePrincipal(context);
        var factory = context.Services.GetService<IAppPasswordPrincipalFactory>()
                      ?? throw new InvalidOperationException(MissingPrincipalMessage(context.Scheme.Name));
        return factory.CreatePrincipalAsync(context);
    }

    /// <summary>Runs <see cref="OnAuthenticated"/>.</summary>
    public virtual ValueTask Authenticated(AppPasswordAuthenticatedContext context) {
        return OnAuthenticated(context);
    }

    internal static string MissingPrincipalMessage(string scheme) {
        return $"The app-password scheme '{scheme}' cannot create a principal: set Events.OnCreatePrincipal in "
               + "AddElarionAppPasswordAuthentication(o => ...), or call "
               + "services.AddElarionIdentityAppPasswordPrincipals<TUser>() to create it through ASP.NET Core Identity.";
    }

    private static ValueTask<AppPasswordMatch?> VerifyWithAppPasswordsAsync(AppPasswordCredentialContext context) {
        var appPasswords = context.Services.GetService<IAppPasswords>()
                           ?? throw new InvalidOperationException(
                               "The app-password scheme checks credentials through IAppPasswords, which is not "
                               + "registered. Call AddElarionAppPasswordsEntityFrameworkCore<TDbContext>() or "
                               + "AddElarionAppPasswords(), or set Events.OnVerifyCredential to a check of your own.");
        return appPasswords.VerifyAsync(context.Secret, context.RequiredKind, context.CancellationToken);
    }
}

/// <summary>
/// Creates the principal of an app-password request when <see cref="AppPasswordAuthenticationEvents.OnCreatePrincipal"/>
/// is not set. <c>AddElarionIdentityAppPasswordPrincipals&lt;TUser&gt;()</c> registers the ASP.NET Core Identity one.
/// </summary>
public interface IAppPasswordPrincipalFactory {
    /// <summary>The principal of <see cref="AppPasswordPrincipalContext.Match"/>'s user, or <see langword="null"/> to refuse.</summary>
    ValueTask<ClaimsPrincipal?> CreatePrincipalAsync(AppPasswordPrincipalContext context);
}
