using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Devices.EntityFrameworkCore;

/// <summary>
/// EF-backed <see cref="IDeviceKeyStore"/> over a <typeparamref name="TDbContext"/> whose model
/// includes <see cref="DeviceKeyEntity"/> via <c>UseElarionDeviceIdentity</c>.
/// </summary>
/// <remarks>
/// A singleton that opens a fresh DI scope per operation: key lookups run inside connection
/// handshakes and provisioning endpoints, outside any handler scope. Writes are
/// change-tracker-free — put is an <c>INSERT … ON CONFLICT DO UPDATE</c> (re-pairing rotates the
/// key), remove is an <c>ExecuteDelete</c>.
/// </remarks>
public sealed class EfCoreDeviceKeyStore<TDbContext>(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider) : IDeviceKeyStore
    where TDbContext : DbContext {
    // Provider- and schema-specific (delimited identifiers, resolved column names), so built once per model.
    private static readonly ConcurrentDictionary<IModel, string> UpsertSqlCache = new();

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>?> GetKeyAsync(string deviceId,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var key = await dbContext.Set<DeviceKeyEntity>()
            .AsNoTracking()
            .Where(entity => entity.DeviceId == deviceId)
            .Select(entity => entity.Key)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        // Convert on the non-null branch only: a null byte[] would otherwise become an EMPTY
        // ReadOnlyMemory and report the device as known.
        return key is null ? null : (ReadOnlyMemory<byte>?)key;
    }

    /// <inheritdoc />
    public async ValueTask PutAsync(string deviceId, ReadOnlyMemory<byte> key,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var sql = UpsertSqlCache.GetOrAdd(
            dbContext.Model,
            static (_, context) => DeviceIdentityEntitySql.BuildKeyUpsertSql(context),
            dbContext);
        await dbContext.Database
            .ExecuteSqlRawAsync(sql, [deviceId, key.ToArray(), timeProvider.GetUtcNow()], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(string deviceId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(deviceId);
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TDbContext>();
        var deleted = await dbContext.Set<DeviceKeyEntity>()
            .Where(entity => entity.DeviceId == deviceId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        return deleted > 0;
    }
}
