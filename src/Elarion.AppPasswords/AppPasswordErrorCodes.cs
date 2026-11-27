namespace Elarion.AppPasswords;

/// <summary>The stable codes of the <c>AppError</c>s <see cref="IAppPasswords"/> returns (ADR-0111).</summary>
public static class AppPasswordErrorCodes {
    /// <summary>
    /// The user already holds <see cref="AppPasswordOptions.MaxPerUser"/> app passwords; a conflict whose data is an
    /// <see cref="AppPasswordLimitReachedData"/>.
    /// </summary>
    public const string LimitReached = "app_password.limit_reached";

    /// <summary>No app password with that id belongs to the user; a not-found, also for another user's id.</summary>
    public const string NotFound = "app_password.not_found";
}

/// <summary>
/// The payload of <see cref="AppPasswordErrorCodes.LimitReached"/>: the cap the user reached, so an application words the
/// answer in its own language without reading the options again.
/// </summary>
/// <remarks>
/// A handler that returns the error over JSON-RPC declares
/// <c>[ProducesError(AppPasswordErrorCodes.LimitReached, ErrorKind.Conflict, typeof(AppPasswordLimitReachedData))]</c>, so
/// the payload is in its error contract and its serializer context.
/// </remarks>
public sealed record AppPasswordLimitReachedData {
    /// <summary>The most app passwords one user may hold, <see cref="AppPasswordOptions.MaxPerUser"/>.</summary>
    public required int MaxPerUser { get; init; }
}
