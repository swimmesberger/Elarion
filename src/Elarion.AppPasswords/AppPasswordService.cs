using Elarion.Abstractions;
using Microsoft.Extensions.Options;

namespace Elarion.AppPasswords;

/// <summary>The <see cref="IAppPasswords"/> over the registered <see cref="IAppPasswordStore"/>.</summary>
internal sealed class AppPasswordService(
    IAppPasswordStore store,
    IOptions<AppPasswordOptions> options,
    TimeProvider timeProvider) : IAppPasswords {
    public async ValueTask<Result<AppPasswordIssued>> CreateAsync(string userId, string name, AppPasswordScope scope,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(scope);
        if (name.Length > AppPasswordOptions.MaxNameLength)
            throw new ArgumentException(
                $"An app password name must be at most {AppPasswordOptions.MaxNameLength} characters.", nameof(name));

        var settings = options.Value;
        var secret = AppPasswordSecrets.Generate(settings.SecretPrefix);
        var record = new AppPasswordRecord {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            Name = name,
            Scope = scope,
            Hash = AppPasswordSecrets.ComputeHash(secret),
            CreatedAt = timeProvider.GetUtcNow(),
        };

        if (!await store.TryAddAsync(record, settings.MaxPerUser, cancellationToken).ConfigureAwait(false))
            return AppError.Conflict(
                $"The user already holds the maximum of {settings.MaxPerUser} app passwords; revoke one first.",
                AppPasswordErrorCodes.LimitReached);

        return new AppPasswordIssued { Password = record.ToInfo(), Secret = secret };
    }

    public ValueTask<IReadOnlyList<AppPasswordInfo>> ListAsync(string userId,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return store.ListAsync(userId, cancellationToken);
    }

    public async ValueTask<Result> RevokeAsync(string userId, Guid id, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return await store.RemoveAsync(userId, id, cancellationToken).ConfigureAwait(false)
            ? Result.Success()
            : AppError.NotFound("The app password was not found.", AppPasswordErrorCodes.NotFound);
    }

    public async ValueTask<AppPasswordMatch?> VerifyAsync(string secret, AppPasswordScope requiredScope,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(requiredScope);
        var record = await FindAsync(secret, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Scope != requiredScope) return null;
        return await AcceptAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AppPasswordMatch?> VerifyAsync(string secret, AppPasswordScopeKind requiredKind,
        CancellationToken cancellationToken = default) {
        if (requiredKind == AppPasswordScopeKind.Other) return null;
        var record = await FindAsync(secret, cancellationToken).ConfigureAwait(false);
        if (record is null || record.Scope.Kind != requiredKind) return null;
        return await AcceptAsync(record, cancellationToken).ConfigureAwait(false);
    }

    private ValueTask<AppPasswordRecord?> FindAsync(string secret, CancellationToken cancellationToken) {
        // An empty or oversized credential is refused before any work: the length cap keeps a hostile header from
        // being hashed, and no issued secret is near it.
        if (string.IsNullOrEmpty(secret) || secret.Length > AppPasswordSecrets.MaxSecretLength)
            return ValueTask.FromResult<AppPasswordRecord?>(null);
        return store.FindByHashAsync(AppPasswordSecrets.ComputeHash(secret), cancellationToken);
    }

    private async ValueTask<AppPasswordMatch> AcceptAsync(AppPasswordRecord record, CancellationToken cancellationToken) {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now - options.Value.LastUseInterval;
        // Checked here first so a polling client causes no write within the interval; the store repeats the check in
        // its conditional write, so two nodes that both pass it still write once.
        if (record.LastUsedAt is null || record.LastUsedAt < staleBefore)
            await store.TouchAsync(record.Id, now, staleBefore, cancellationToken).ConfigureAwait(false);
        return new AppPasswordMatch { Id = record.Id.ToString(), UserId = record.UserId, Scope = record.Scope };
    }
}
