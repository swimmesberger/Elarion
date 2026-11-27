using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.AppPasswords.AspNetCore;

/// <summary>
/// The ASP.NET Core Identity <see cref="IAppPasswordPrincipalFactory"/>: finds the user by id, refuses a missing or
/// locked-out one, and creates the principal with the application's <see cref="IUserClaimsPrincipalFactory{TUser}"/>.
/// Registered by <c>AddElarionIdentityAppPasswordPrincipals&lt;TUser&gt;()</c>.
/// </summary>
/// <typeparam name="TUser">The Identity user type.</typeparam>
internal sealed class IdentityAppPasswordPrincipalFactory<TUser> : IAppPasswordPrincipalFactory where TUser : class {
    public async ValueTask<ClaimsPrincipal?> CreatePrincipalAsync(AppPasswordPrincipalContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var users = context.Services.GetRequiredService<UserManager<TUser>>();
        var user = await users.FindByIdAsync(context.Match.UserId).ConfigureAwait(false);
        if (user is null) return null;
        if (users.SupportsUserLockout && await users.IsLockedOutAsync(user).ConfigureAwait(false)) return null;
        var claims = context.Services.GetRequiredService<IUserClaimsPrincipalFactory<TUser>>();
        return await claims.CreateAsync(user).ConfigureAwait(false);
    }
}
