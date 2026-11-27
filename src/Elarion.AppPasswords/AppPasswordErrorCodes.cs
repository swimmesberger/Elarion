namespace Elarion.AppPasswords;

/// <summary>The stable codes of the <c>AppError</c>s <see cref="IAppPasswords"/> returns (ADR-0111).</summary>
public static class AppPasswordErrorCodes {
    /// <summary>The user already holds <see cref="AppPasswordOptions.MaxPerUser"/> app passwords; a conflict.</summary>
    public const string LimitReached = "app_password.limit_reached";

    /// <summary>No app password with that id belongs to the user; a not-found, also for another user's id.</summary>
    public const string NotFound = "app_password.not_found";
}
