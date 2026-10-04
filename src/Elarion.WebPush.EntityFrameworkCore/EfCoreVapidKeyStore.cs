using System.Collections.Concurrent;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.WebPush.EntityFrameworkCore;

/// <summary>
/// EF-backed <see cref="IVapidKeyStore"/> over a <typeparamref name="TDbContext"/> whose model includes
/// <see cref="VapidKeyEntity"/> via <c>UseElarionWebPush</c>.
/// </summary>
/// <remarks>
/// <para>
/// The first-use race is settled by the primary key: every node inserts its candidate with
/// <c>ON CONFLICT DO NOTHING</c> and then reads the row back, so all of them adopt the single winner.
/// </para>
/// <para>
/// The private key is protected at rest through the framework's protection seam, the registered
/// <see cref="ISettingValueProtector"/> (ADR-0078; <c>AddElarionSettingsDataProtection()</c> is the shipped
/// implementation), bound to the key pair's public half so a payload copied onto another row does not decrypt.
/// The store fails closed: without a protector it cannot be constructed, and a payload that does not decrypt
/// throws <see cref="SettingProtectionException"/> instead of generating a replacement pair (which would
/// invalidate every subscription). A legacy row written before protection existed (no
/// <see cref="VapidKeyEntity.Protection"/>) is accepted once and re-protected in place, as is a payload under a
/// retired key.
/// </para>
/// <para>
/// Unlike the subscription store, a singleton on its own DI scope, deliberately outside any caller's unit of
/// work: the provider caches the pair for the life of the process, so it must be stored for good before it is
/// used — a caller's rollback must never un-store a key that browsers already subscribed against. To keep that
/// first write away from callers entirely (on SQLite it would wait for a caller's write lock), the pair is
/// resolved when the host starts; see <c>AddElarionWebPushEntityFrameworkCore</c>.
/// </para>
/// </remarks>
public sealed class EfCoreVapidKeyStore<TDbContext> : IVapidKeyStore
    where TDbContext : DbContext {
    /// <summary>The root of the purpose string the private key is protected under.</summary>
    public const string PurposePrefix = "Elarion.WebPush.VapidKey";

    private static readonly ConcurrentDictionary<IModel, string> InsertSqlCache = new();

    private readonly ISettingValueProtector _protector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the store.</summary>
    /// <param name="scopeFactory">Opens the store's own scope per operation.</param>
    /// <param name="timeProvider">The clock stamping a newly stored pair.</param>
    /// <param name="protector">
    /// The at-rest protector for the private key; required (there is no unprotected fallback).
    /// </param>
    /// <exception cref="SettingProtectionException">No <see cref="ISettingValueProtector"/> is registered.</exception>
    public EfCoreVapidKeyStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ISettingValueProtector? protector = null) {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _protector = protector ?? throw new SettingProtectionException(
            "The Web Push VAPID private key is protected at rest, but no ISettingValueProtector is registered. "
            + "Call AddElarionSettingsDataProtection() (Elarion.Settings.DataProtection) or register your own "
            + "protector, or configure WebPushOptions.PublicKey/PrivateKey from a secret store.");
    }

    /// <inheritdoc />
    public async ValueTask<VapidKeys?> GetAsync(CancellationToken cancellationToken = default) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        return await ReadAsync(dbContext, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<VapidKeys> GetOrAddAsync(VapidKeys candidate, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = InsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => WebPushEntitySql.BuildVapidKeyInsertSql(context),
            dbContext);
        var parameters = WebPushEntitySql.Parameters(dbContext, typeof(VapidKeyEntity),
            (nameof(VapidKeyEntity.Name), VapidKeyEntity.DefaultName),
            (nameof(VapidKeyEntity.PublicKey), candidate.PublicKey),
            (nameof(VapidKeyEntity.PrivateKey), _protector.Protect(Purpose(candidate.PublicKey), candidate.PrivateKey)),
            (nameof(VapidKeyEntity.Protection), _protector.Scheme),
            (nameof(VapidKeyEntity.CreatedOnUtc), _timeProvider.GetUtcNow()));
        await dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(dbContext, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The VAPID key row vanished right after it was inserted.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// The private key is protected with the same purpose as a generated pair, so an imported pair is
    /// indistinguishable from one the store minted. Atomicity rests on the primary key: the insert is
    /// <c>ON CONFLICT DO NOTHING</c> and the stored row is read back, so concurrent imports of different pairs
    /// leave one winner and refuse the rest. An overwrite is an explicit, last-writer-wins replacement.
    /// </remarks>
    public async ValueTask<VapidKeyImportResult> ImportAsync(
        VapidKeys keys, bool overwrite = false, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(keys);
        keys.Validate();
        await using var scope = _scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = InsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => WebPushEntitySql.BuildVapidKeyInsertSql(context),
            dbContext);
        var now = _timeProvider.GetUtcNow();
        var payload = _protector.Protect(Purpose(keys.PublicKey), keys.PrivateKey);
        var parameters = WebPushEntitySql.Parameters(dbContext, typeof(VapidKeyEntity),
            (nameof(VapidKeyEntity.Name), VapidKeyEntity.DefaultName),
            (nameof(VapidKeyEntity.PublicKey), keys.PublicKey),
            (nameof(VapidKeyEntity.PrivateKey), payload),
            (nameof(VapidKeyEntity.Protection), _protector.Scheme),
            (nameof(VapidKeyEntity.CreatedOnUtc), now));
        var inserted = await dbContext.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken)
            .ConfigureAwait(false);
        if (inserted > 0) return VapidKeyImportResult.Imported;

        var stored = await ReadAsync(dbContext, cancellationToken).ConfigureAwait(false);
        // The row vanished between the conflict and the read (a manual delete): insert it afresh.
        if (stored is null) return await ImportAsync(keys, overwrite, cancellationToken).ConfigureAwait(false);

        if (InMemoryVapidKeyStore.Same(stored, keys)) return VapidKeyImportResult.Unchanged;

        if (!overwrite) return VapidKeyImportResult.Refused;

        var scheme = _protector.Scheme;
        var replaced = await dbContext.Set<VapidKeyEntity>()
            .Where(entity => entity.Name == VapidKeyEntity.DefaultName)
            .ExecuteUpdateAsync(
                set => set.SetProperty(entity => entity.PublicKey, keys.PublicKey)
                    .SetProperty(entity => entity.PrivateKey, payload)
                    .SetProperty(entity => entity.Protection, scheme)
                    .SetProperty(entity => entity.CreatedOnUtc, now),
                cancellationToken)
            .ConfigureAwait(false);
        // The row vanished between the conflict and the update (a manual delete): insert it afresh.
        return replaced > 0
            ? VapidKeyImportResult.Replaced
            : await ImportAsync(keys, overwrite, cancellationToken).ConfigureAwait(false);
    }

    private static string Purpose(string publicKey) {
        return $"{PurposePrefix}/{VapidKeyEntity.DefaultName}/{publicKey}";
    }

    private async Task<VapidKeys?> ReadAsync(TDbContext dbContext, CancellationToken cancellationToken) {
        var row = await dbContext.Set<VapidKeyEntity>()
            .AsNoTracking()
            .Where(entity => entity.Name == VapidKeyEntity.DefaultName)
            .Select(entity => new { entity.PublicKey, entity.PrivateKey, entity.Protection })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null) return null;

        if (row.Protection is null) {
            // A row from before protection existed: plaintext, accepted once and protected in place.
            await RewriteAsync(dbContext, row.PublicKey, row.PrivateKey, row.Protection, row.PrivateKey,
                cancellationToken).ConfigureAwait(false);
            return new VapidKeys { PublicKey = row.PublicKey, PrivateKey = row.PrivateKey };
        }

        if (!string.Equals(row.Protection, _protector.Scheme, StringComparison.Ordinal))
            throw new SettingProtectionException(
                $"The stored VAPID private key was protected with scheme '{row.Protection}', but the registered "
                + $"protector's scheme is '{_protector.Scheme}'.");

        var unprotected = _protector.Unprotect(Purpose(row.PublicKey), row.PrivateKey);
        if (unprotected.RequiresReprotection)
            await RewriteAsync(dbContext, row.PublicKey, row.PrivateKey, row.Protection, unprotected.Plaintext,
                cancellationToken).ConfigureAwait(false);
        return new VapidKeys { PublicKey = row.PublicKey, PrivateKey = unprotected.Plaintext };
    }

    // Guarded by the exact stored value, so a concurrent writer (another node re-protecting the same row) wins.
    private Task RewriteAsync(TDbContext dbContext, string publicKey, string storedPrivateKey, string? storedProtection,
        string plaintext, CancellationToken cancellationToken) {
        var payload = _protector.Protect(Purpose(publicKey), plaintext);
        var scheme = _protector.Scheme;
        return dbContext.Set<VapidKeyEntity>()
            .Where(entity => entity.Name == VapidKeyEntity.DefaultName
                             && entity.PrivateKey == storedPrivateKey
                             && entity.Protection == storedProtection)
            .ExecuteUpdateAsync(
                set => set.SetProperty(entity => entity.PrivateKey, payload)
                    .SetProperty(entity => entity.Protection, scheme),
                cancellationToken);
    }
}
