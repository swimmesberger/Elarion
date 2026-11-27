namespace Elarion.AppPasswords;

/// <summary>
/// Where app passwords are kept (ADR-0111). <c>Elarion.AppPasswords.EntityFrameworkCore</c> provides the store on the
/// application's <c>DbContext</c>, <c>AddElarionInMemoryAppPasswordStore()</c> one for tests. An application whose
/// credentials live elsewhere replaces the scheme's credential check instead and needs no store.
/// </summary>
public interface IAppPasswordStore {
    /// <summary>
    /// Adds <paramref name="record"/> unless its user already holds <paramref name="maxPerUser"/> app passwords. The
    /// check and the insert are one atomic step, so concurrent creations on several nodes cannot pass the cap.
    /// </summary>
    /// <returns><see langword="true"/> when added; <see langword="false"/> when the cap was reached.</returns>
    ValueTask<bool> TryAddAsync(AppPasswordRecord record, int maxPerUser, CancellationToken cancellationToken = default);

    /// <summary>Lists a user's app passwords, oldest first.</summary>
    ValueTask<IReadOnlyList<AppPasswordInfo>> ListAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the app password <paramref name="id"/> if it belongs to <paramref name="userId"/>.</summary>
    /// <returns><see langword="true"/> when a row was deleted.</returns>
    ValueTask<bool> RemoveAsync(string userId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>Finds the app password whose secret has <paramref name="hash"/>.</summary>
    ValueTask<AppPasswordRecord?> FindByHashAsync(string hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the last use of <paramref name="id"/> to <paramref name="usedAt"/> when it is unset or earlier than
    /// <paramref name="staleBefore"/>, as one conditional write, so concurrent requests on several nodes write it once.
    /// </summary>
    ValueTask TouchAsync(Guid id, DateTimeOffset usedAt, DateTimeOffset staleBefore,
        CancellationToken cancellationToken = default);
}
