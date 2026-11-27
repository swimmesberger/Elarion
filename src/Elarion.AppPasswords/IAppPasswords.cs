using Elarion.Abstractions;

namespace Elarion.AppPasswords;

/// <summary>
/// Issues, lists, revokes and verifies app passwords (ADR-0111): credentials a person creates in the application for a
/// calendar app (<see cref="AppPasswordScope.Dav"/>) or for the feed of one calendar
/// (<see cref="AppPasswordScope.CalendarFeed(string, string)"/>). Management is the application's: it calls this from
/// its own-account handlers, with the current user's id.
/// </summary>
/// <example>
/// <code>
/// Result&lt;AppPasswordIssued&gt; issued = await appPasswords.CreateAsync(user.UserId, "iPhone", AppPasswordScope.Dav, ct);
/// if (!issued.IsSuccess) return issued.Error;            // app_password.limit_reached
/// return new CreatedAppPassword(issued.Value.Password.Id, issued.Value.Secret);   // shown once
/// </code>
/// </example>
public interface IAppPasswords {
    /// <summary>
    /// Creates an app password for <paramref name="userId"/> and returns it with its secret, the only time the secret
    /// is readable. Fails with <see cref="AppPasswordErrorCodes.LimitReached"/> (a conflict) when the user already
    /// holds <see cref="AppPasswordOptions.MaxPerUser"/> app passwords. The password is stored when this returns,
    /// independent of the caller's transaction.
    /// </summary>
    /// <param name="userId">The owner, as <c>ICurrentUser.UserId</c>.</param>
    /// <param name="name">A name the user recognises, 1 to <see cref="AppPasswordOptions.MaxNameLength"/> characters.</param>
    /// <param name="scope">What the password may be used for.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentException"><paramref name="userId"/> or <paramref name="name"/> is empty, or one of them is too long (<see cref="AppPasswordOptions.MaxUserIdLength"/>, <see cref="AppPasswordOptions.MaxNameLength"/>).</exception>
    ValueTask<Result<AppPasswordIssued>> CreateAsync(string userId, string name, AppPasswordScope scope,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the app passwords of <paramref name="userId"/>, oldest first, without their secrets.</summary>
    ValueTask<IReadOnlyList<AppPasswordInfo>> ListAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes (deletes) the app password <paramref name="id"/> of <paramref name="userId"/>; the next request with it is
    /// refused. Fails with <see cref="AppPasswordErrorCodes.NotFound"/> when no such password belongs to the user,
    /// which is also the answer for another user's password.
    /// </summary>
    ValueTask<Result> RevokeAsync(string userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks <paramref name="secret"/> against the store and returns its match when it exists and its scope equals
    /// <paramref name="requiredScope"/>; <see langword="null"/> otherwise. Records the use at most once per
    /// <see cref="AppPasswordOptions.LastUseInterval"/>.
    /// </summary>
    ValueTask<AppPasswordMatch?> VerifyAsync(string secret, AppPasswordScope requiredScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks <paramref name="secret"/> as the other overload does, accepting any scope of <paramref name="requiredKind"/>.
    /// A feed URL holds only the token, so a feed request is checked for <see cref="AppPasswordScopeKind.CalendarFeed"/>
    /// and the calendar is read from the match's scope.
    /// </summary>
    ValueTask<AppPasswordMatch?> VerifyAsync(string secret, AppPasswordScopeKind requiredKind,
        CancellationToken cancellationToken = default);
}
