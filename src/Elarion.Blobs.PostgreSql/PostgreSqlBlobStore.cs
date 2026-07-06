using System.Collections.Concurrent;
using System.Data;
using Elarion.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// PostgreSQL-backed <see cref="IBlobStore"/> implementation.
/// </summary>
/// <typeparam name="TDbContext">The EF Core context that owns the blob tables.</typeparam>
/// <remarks>
/// Content is stored in a <c>bytea</c> column. Writes stream a seekable source straight through the
/// Npgsql binary protocol without buffering. A non-seekable source streams without buffering too when
/// the caller supplies a <see cref="BlobUploadRequest.ContentLength"/> hint (the protocol requires the
/// length up front; the actual bytes are verified against the hint so the recorded size stays
/// truthful); without a hint it is buffered first only to learn its length. Reads <b>stream</b> as
/// well: <see cref="OpenReadAsync"/> opens a dedicated connection <b>cloned from the context's own
/// connection</b> and reads through <see cref="CommandBehavior.SequentialAccess"/> +
/// <c>NpgsqlDataReader.GetStream</c>, with the reader, command, and connection owned by the returned
/// <see cref="BlobDownload"/> so they live exactly as long as the caller reads. The scoped context's
/// shared connection cannot host such a reader (it would outlive the call and block other context
/// work), so the store clones a sibling: <see cref="ICloneable.Clone"/> on an
/// <see cref="NpgsqlConnection"/> reuses the connection's owning <see cref="NpgsqlDataSource"/> —
/// same pool, same type mapping, same auth callbacks, and the same database by construction — so no
/// separately configured data source can drift from the context (ADR-0041). The one exception is a
/// read inside a caller-owned ambient transaction: it must share that transaction's connection to see
/// the caller's own uncommitted writes, so it stays buffered.
/// </remarks>
public sealed class PostgreSqlBlobStore<TDbContext>(
    TDbContext dbContext,
    ILogger<PostgreSqlBlobStore<TDbContext>> logger,
    TimeProvider timeProvider) : IBlobStore, IBlobLifecycle
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async Task<BlobRef> SaveAsync(
        BlobUploadRequest request,
        Stream content,
        CancellationToken cancellationToken) {
        ValidateUploadRequest(request);
        ArgumentNullException.ThrowIfNull(content);

        // The bytea bind needs the exact length up front. A seekable source streams without
        // buffering. A non-seekable source streams without buffering too when the caller supplies a
        // ContentLength hint (verified against the actual bytes); otherwise it is buffered here only
        // to learn the length. We never dispose the caller's stream — they own it.
        if (content.CanSeek) {
            return await WriteAsync(request, content, content.Length - content.Position, hinted: null, cancellationToken);
        }

        if (request.ContentLength is long hint) {
            ArgumentOutOfRangeException.ThrowIfNegative(hint, nameof(request));
            var hintedStream = new HintedLengthStream(content, hint);
            return await WriteAsync(request, hintedStream, hint, hintedStream, cancellationToken);
        }

        var bufferedStream = new MemoryStream();
        try {
            await content.CopyToAsync(bufferedStream, cancellationToken);
            bufferedStream.Position = 0;
            return await WriteAsync(request, bufferedStream, bufferedStream.Length, hinted: null, cancellationToken);
        }
        finally {
            await bufferedStream.DisposeAsync();
        }
    }

    private async Task<BlobRef> WriteAsync(
        BlobUploadRequest request,
        Stream writeStream,
        long size,
        HintedLengthStream? hinted,
        CancellationToken cancellationToken) {
        var blobId = await SaveBlobAsync(
            request,
            size,
            async id => {
                await UpsertContentStreamAsync(id, writeStream, cancellationToken);
                if (hinted is not null) {
                    await VerifyHintAsync(hinted, size, cancellationToken);
                }
            },
            cancellationToken);

        logger.LogInformation(
            "Blob stored: {BlobId} ({Container}/{Name}, {Size} bytes)",
            blobId,
            request.Container,
            request.Name,
            size);

        return new BlobRef { Value = blobId };
    }

    private static async Task VerifyHintAsync(HintedLengthStream hinted, long hint, CancellationToken cancellationToken) {
        if (hinted.BytesRead != hint || await hinted.HasUnreadInnerDataAsync(cancellationToken)) {
            throw new InvalidOperationException(
                $"The actual content length did not match the declared " +
                $"{nameof(BlobUploadRequest)}.{nameof(BlobUploadRequest.ContentLength)} hint of {hint} bytes.");
        }
    }

    /// <inheritdoc />
    public async Task<BlobDownload?> OpenReadAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        ValidateBlobRef(blobRef);

        var metadata = await dbContext.Set<StoredBlob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(blob => blob.Id == blobRef.Value, cancellationToken);

        if (metadata is null) {
            return null;
        }

        // A read inside a caller-owned ambient transaction must share that transaction's connection to see
        // the caller's own uncommitted writes — and the scoped connection cannot host a reader that outlives
        // this call — so the transactional path stays buffered.
        if (dbContext.Database.CurrentTransaction is not null) {
            return await OpenBufferedReadAsync(metadata, blobRef, cancellationToken);
        }

        // Streaming path: a dedicated connection whose lifetime is owned by the returned BlobDownload, so
        // the content flows straight from the wire without materializing the blob in memory.
        var connection = await OpenStreamingConnectionAsync(cancellationToken);
        NpgsqlCommand? command = null;
        NpgsqlDataReader? reader = null;
        var transferred = false;
        try {
            command = connection.CreateCommand();
            command.CommandText = GetContentSql(dbContext).Select;
            command.Parameters.Add(new NpgsqlParameter { Value = blobRef.Value });

            reader = await command.ExecuteReaderAsync(
                CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) {
                return null;
            }

            var content = reader.GetStream(0);
            var download = new BlobDownload(
                ToMetadata(metadata), content, new StreamingReadResources(connection, command, reader));
            transferred = true;

            return download;
        }
        finally {
            if (!transferred) {
                if (reader is not null) {
                    await reader.DisposeAsync();
                }

                if (command is not null) {
                    await command.DisposeAsync();
                }

                await connection.DisposeAsync();
            }
        }
    }

    private async Task<BlobDownload?> OpenBufferedReadAsync(
        StoredBlob metadata,
        BlobRef blobRef,
        CancellationToken cancellationToken) {
        var connection = await GetOpenNpgsqlConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = GetCurrentNpgsqlTransaction();
        command.CommandText = GetContentSql(dbContext).Select;
        command.Parameters.Add(new NpgsqlParameter { Value = blobRef.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }

        var data = (byte[])reader[0];
        return new BlobDownload(ToMetadata(metadata), new MemoryStream(data, writable: false));
    }

    /// <summary>
    /// The backend resources behind a streaming read, disposed by <see cref="BlobDownload"/> after the
    /// content stream. Idempotent so a double-disposed download stays safe.
    /// </summary>
    private sealed class StreamingReadResources(
        NpgsqlConnection connection,
        NpgsqlCommand command,
        NpgsqlDataReader reader) : IAsyncDisposable {
        private int _disposed;

        public async ValueTask DisposeAsync() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) {
                return;
            }

            await reader.DisposeAsync().ConfigureAwait(false);
            await command.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        ValidateBlobRef(blobRef);

        var blob = await dbContext.Set<StoredBlob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(storedBlob => storedBlob.Id == blobRef.Value, cancellationToken);

        return blob is null ? null : ToMetadata(blob);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        ValidateBlobRef(blobRef);

        var blob = await dbContext.Set<StoredBlob>()
            .FirstOrDefaultAsync(storedBlob => storedBlob.Id == blobRef.Value, cancellationToken);

        if (blob is null) {
            return false;
        }

        dbContext.Set<StoredBlob>().Remove(blob);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Blob deleted: {BlobId}", blobRef.Value);

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        ValidateBlobRef(blobRef);

        return await dbContext.Set<StoredBlob>()
            .AsNoTracking()
            .AnyAsync(blob => blob.Id == blobRef.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BlobListing> ListAsync(BlobListRequest request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Container, nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.PageSize, nameof(request));

        // Phase 1: one grouped, keyset-paginated query computes the page of entries — blob names and,
        // under a delimiter, the distinct virtual-directory prefixes — in ordinal (COLLATE "C") order.
        // Fetching one extra row tells us whether a next page exists without a trailing empty page.
        var entries = await QueryEntriesAsync(request, request.PageSize + 1, cancellationToken);
        var hasMore = entries.Count > request.PageSize;
        if (hasMore) {
            entries.RemoveAt(entries.Count - 1);
        }

        // Phase 2: hydrate metadata for the blob entries (an entry deleted between the two phases is
        // simply omitted). Prefix entries need no second query.
        var blobNames = entries.Where(e => !e.IsPrefix).Select(e => e.Entry).ToList();
        var rowsByName = blobNames.Count == 0
            ? []
            : (await dbContext.Set<StoredBlob>()
                .AsNoTracking()
                .Where(b => b.Container == request.Container && blobNames.Contains(b.Name))
                .ToListAsync(cancellationToken))
                .ToDictionary(b => b.Name, StringComparer.Ordinal);

        var blobs = new List<BlobMetadata>(blobNames.Count);
        var prefixes = new List<string>();
        foreach (var entry in entries) {
            if (entry.IsPrefix) {
                prefixes.Add(entry.Entry);
            }
            else if (rowsByName.TryGetValue(entry.Entry, out var row)) {
                blobs.Add(ToMetadata(row));
            }
        }

        return new BlobListing {
            Blobs = blobs,
            Prefixes = prefixes,
            ContinuationToken = hasMore && entries.Count > 0 ? EncodeListToken(entries[^1]) : null,
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ListContainersAsync(CancellationToken cancellationToken) =>
        await dbContext.Set<StoredBlob>()
            .AsNoTracking()
            .Select(blob => blob.Container)
            .Distinct()
            .OrderBy(container => container)
            .ToListAsync(cancellationToken);

    private async Task<List<BlobListEntryRow>> QueryEntriesAsync(
        BlobListRequest request,
        int limit,
        CancellationToken cancellationToken) {
        // The listing SQL is built from the EF model (like the content SQL) so table/column overrides
        // and the snake-case toggle apply. Ordering and the keyset comparison use COLLATE "C" so pages
        // are ordinal-ordered and pagination is stable regardless of the database's default collation.
        // Not cached: listing is a browse/ops surface, not a hot path.
        var entityType = dbContext.Model.FindEntityType(typeof(StoredBlob))
            ?? throw new InvalidOperationException(
                "The blob entity is not mapped. Call modelBuilder.UseElarionBlobStorage() in OnModelCreating.");
        var sqlHelper = dbContext.GetService<ISqlGenerationHelper>();
        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The blob entity is not mapped to a table.");
        var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The {nameof(StoredBlob)}.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The {nameof(StoredBlob)}.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, entityType.GetSchema());
        var name = Column(nameof(StoredBlob.Name));
        var container = Column(nameof(StoredBlob.Container));
        var state = Column(nameof(StoredBlob.State));

        var args = new List<object>();
        string P(object value) {
            args.Add(value);
            return "{" + (args.Count - 1) + "}";
        }

        var containerParam = P(request.Container);
        var prefixParam = P(request.Prefix ?? string.Empty);
        var (afterEntry, afterIsPrefix) = DecodeListToken(request.ContinuationToken);

        var filter = $"{container} = {containerParam} AND substr({name}, 1, length({prefixParam})) = {prefixParam}";
        if (request.State is { } stateFilter) {
            filter += $" AND {state} = {P((int)stateFilter)}";
        }

        string sql;
        if (request.Delimiter is { Length: > 0 } delimiter) {
            var delimiterParam = P(delimiter);
            // Position of the delimiter within the name segment after the prefix; > 0 → the row rolls
            // up into a (delimiter-inclusive) virtual-directory prefix instead of a blob entry.
            var rest = $"substr({name}, length({prefixParam}) + 1)";
            var position = $"strpos({rest}, {delimiterParam})";
            sql =
                $"SELECT e.\"Entry\", e.\"IsPrefix\" FROM (" +
                $"SELECT DISTINCT CASE WHEN {position} > 0 " +
                $"THEN substr({name}, 1, length({prefixParam}) + {position} + length({delimiterParam}) - 1) " +
                $"ELSE {name} END AS \"Entry\", {position} > 0 AS \"IsPrefix\" " +
                $"FROM {table} WHERE {filter}) AS e";
            if (afterEntry is not null) {
                sql += $" WHERE (e.\"Entry\" COLLATE \"C\", e.\"IsPrefix\") > ({P(afterEntry)}, {P(afterIsPrefix)})";
            }

            sql += $" ORDER BY e.\"Entry\" COLLATE \"C\", e.\"IsPrefix\" LIMIT {P(limit)}";
        }
        else {
            sql = $"SELECT {name} AS \"Entry\", false AS \"IsPrefix\" FROM {table} WHERE {filter}";
            if (afterEntry is not null) {
                sql += $" AND {name} COLLATE \"C\" > {P(afterEntry)}";
            }

            sql += $" ORDER BY {name} COLLATE \"C\" LIMIT {P(limit)}";
        }

        return await dbContext.Database
            .SqlQueryRaw<BlobListEntryRow>(sql, args.ToArray())
            .ToListAsync(cancellationToken);
    }

    private static string EncodeListToken(BlobListEntryRow entry) =>
        (entry.IsPrefix ? "p|" : "b|") + entry.Entry;

    private static (string? Entry, bool IsPrefix) DecodeListToken(string? token) {
        if (token is null) {
            return (null, false);
        }

        if (token.Length < 2 || token[1] != '|' || token[0] is not ('b' or 'p')) {
            throw new ArgumentException("The continuation token is not from this store.", nameof(token));
        }

        return (token[2..], token[0] == 'p');
    }

    /// <inheritdoc />
    public async Task<bool> CommitAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        ValidateBlobRef(blobRef);

        var blob = await dbContext.Set<StoredBlob>()
            .FirstOrDefaultAsync(storedBlob => storedBlob.Id == blobRef.Value, cancellationToken);

        if (blob is null) {
            return false;
        }

        if (blob.State == BlobLifecycleState.Committed) {
            return true;
        }

        logger.LogDebug("Committing blob {BlobId}", blobRef.Value);

        // Within the caller's transaction, flip the pending row to committed immediately with a guarded
        // ExecuteUpdate rather than only mutating the tracked entity. This does two things the deferred
        // tracked mutation could not: it participates in the caller's transaction (so the promote and the
        // entity insert still commit atomically), and it takes the row's write lock for the rest of that
        // transaction — so the garbage collector's set-based delete (which re-checks State == Pending)
        // cannot reclaim the still-Pending row in the window between commit and the caller's
        // SaveChangesAsync. The guard keeps it idempotent (a concurrent commit that already flipped the row
        // affects zero rows, and we still report success). Keep the tracked entity in sync so a subsequent
        // read on this context sees the committed state.
        if (dbContext.Database.CurrentTransaction is not null) {
            await dbContext.Set<StoredBlob>()
                .Where(storedBlob => storedBlob.Id == blobRef.Value && storedBlob.State == BlobLifecycleState.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(storedBlob => storedBlob.State, BlobLifecycleState.Committed)
                        .SetProperty(storedBlob => storedBlob.ExpiresAt, (DateTimeOffset?)null),
                    cancellationToken);

            blob.State = BlobLifecycleState.Committed;
            blob.ExpiresAt = null;
            dbContext.Entry(blob).State = EntityState.Unchanged;
            return true;
        }

        // No ambient transaction: mutate the tracked entity so the change is persisted by the caller's next
        // SaveChangesAsync (its own implicit transaction), keeping the promote atomic with the entity insert.
        blob.State = BlobLifecycleState.Committed;
        blob.ExpiresAt = null;

        return true;
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredPendingAsync(
        DateTimeOffset olderThanUtc,
        int batchSize,
        CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Bounded set-based delete committed independently of any caller transaction. The id scan rides
        // the partial index; the delete re-checks State == Pending so a blob committed between the scan
        // and the delete is left alone (the garbage-collection-versus-commit guard). Content rows are
        // removed by the blob_contents -> stored_blobs ON DELETE CASCADE foreign key.
        var ids = await dbContext.Set<StoredBlob>()
            .AsNoTracking()
            .Where(blob => blob.State == BlobLifecycleState.Pending
                && blob.ExpiresAt != null
                && blob.ExpiresAt < olderThanUtc)
            .OrderBy(blob => blob.ExpiresAt)
            .Take(batchSize)
            .Select(blob => blob.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0) {
            return 0;
        }

        var deleted = await dbContext.Set<StoredBlob>()
            .Where(blob => ids.Contains(blob.Id)
                && blob.State == BlobLifecycleState.Pending
                && blob.ExpiresAt != null
                && blob.ExpiresAt < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken);

        if (deleted > 0) {
            logger.LogInformation("Garbage collected {Count} expired pending blob(s).", deleted);
        }

        return deleted;
    }

    private async Task<string> SaveBlobAsync(
        BlobUploadRequest request,
        long size,
        Func<string, Task> saveContent,
        CancellationToken cancellationToken) {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var blobId = await UpsertMetadataAsync(request, size, cancellationToken);
        await saveContent(blobId);

        if (transaction is not null) {
            await transaction.CommitAsync(cancellationToken);
        }

        return blobId;
    }

    private async Task<string> UpsertMetadataAsync(
        BlobUploadRequest request,
        long size,
        CancellationToken cancellationToken) {
        var existing = await dbContext.Set<StoredBlob>()
            .FirstOrDefaultAsync(
                blob => blob.Container == request.Container && blob.Name == request.Name,
                cancellationToken);
        var createdAt = timeProvider.GetUtcNow();

        // The lifecycle state and expiry come from the request: a plain save is Committed (no expiry),
        // while a pre-upload transport saves Pending with an ExpiresAt. Expiry is only retained for
        // pending blobs so a committed blob is never accidentally reclaimed.
        var state = request.InitialState;
        var expiresAt = state == BlobLifecycleState.Pending ? request.ExpiresAt : null;

        if (existing is not null) {
            existing.ContentType = request.ContentType;
            existing.Size = size;
            existing.CreatedAt = createdAt;
            existing.State = state;
            existing.ExpiresAt = expiresAt;
            existing.OwnerId = request.OwnerId;

            logger.LogDebug(
                "Updating existing blob {BlobId} ({Container}/{Name})",
                existing.Id,
                request.Container,
                request.Name);
            await dbContext.SaveChangesAsync(cancellationToken);

            return existing.Id;
        }

        var blobId = Guid.CreateVersion7().ToString();
        dbContext.Set<StoredBlob>().Add(new StoredBlob {
            Id = blobId,
            Container = request.Container,
            Name = request.Name,
            ContentType = request.ContentType,
            Size = size,
            CreatedAt = createdAt,
            State = state,
            ExpiresAt = expiresAt,
            OwnerId = request.OwnerId,
        });

        logger.LogDebug("Creating new blob {BlobId} ({Container}/{Name})", blobId, request.Container, request.Name);
        await dbContext.SaveChangesAsync(cancellationToken);

        return blobId;
    }

    private async Task UpsertContentStreamAsync(
        string blobId,
        Stream content,
        CancellationToken cancellationToken) {
        var connection = await GetOpenNpgsqlConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = GetCurrentNpgsqlTransaction();
        command.CommandText = GetContentSql(dbContext).Upsert;
        command.Parameters.Add(new NpgsqlParameter { Value = blobId });
        command.Parameters.Add(new NpgsqlParameter { Value = content, NpgsqlDbType = NpgsqlDbType.Bytea });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The content SQL is built from the EF model (not hard-coded identifiers) so the UseElarionBlobStorage
    // table/column overrides and the snake-case toggle apply to the raw Npgsql content path too. Built once
    // per model and reused.
    private static readonly ConcurrentDictionary<IModel, ContentSql> ContentSqlCache = new();

    internal sealed record ContentSql(string Select, string Upsert);

    internal static ContentSql GetContentSql(DbContext context) =>
        ContentSqlCache.GetOrAdd(context.Model, static (_, ctx) => BuildContentSql(ctx), context);

    private static ContentSql BuildContentSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(BlobContentRow))
            ?? throw new InvalidOperationException(
                "The blob content entity is not mapped. Call modelBuilder.UseElarionBlobStorage() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The blob content entity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The {nameof(BlobContentRow)}.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The {nameof(BlobContentRow)}.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var blobId = Column(nameof(BlobContentRow.BlobId));
        var data = Column(nameof(BlobContentRow.Data));

        return new ContentSql(
            Select: $"SELECT {data} FROM {table} WHERE {blobId} = $1",
            Upsert: $"INSERT INTO {table} ({blobId}, {data}) VALUES ($1, $2) " +
                $"ON CONFLICT ({blobId}) DO UPDATE SET {data} = EXCLUDED.{data}");
    }

    /// <summary>
    /// Opens the dedicated connection a streaming read hands to <see cref="BlobDownload"/>, cloned from
    /// the context's own connection.
    /// </summary>
    /// <remarks>
    /// <see cref="ICloneable.Clone"/> on an <see cref="NpgsqlConnection"/> creates the sibling from the
    /// connection's owning <see cref="NpgsqlDataSource"/> (Npgsql builds one internally even for a plain
    /// connection string), so the clone shares the context's pool, type mapping, and auth callbacks —
    /// and targets the same database by construction. The context connection is opened first so its
    /// data source is resolved before cloning (mirroring <see cref="GetOpenNpgsqlConnectionAsync"/>,
    /// which the write path already relies on).
    /// </remarks>
    private async Task<NpgsqlConnection> OpenStreamingConnectionAsync(CancellationToken cancellationToken) {
        var contextConnection = await GetOpenNpgsqlConnectionAsync(cancellationToken);
        var connection = (NpgsqlConnection)((ICloneable)contextConnection).Clone();
        try {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task<NpgsqlConnection> GetOpenNpgsqlConnectionAsync(CancellationToken cancellationToken) {
        var dbConnection = dbContext.Database.GetDbConnection();
        var connection = dbConnection as NpgsqlConnection
            ?? throw new InvalidOperationException(
                $"Expected {nameof(NpgsqlConnection)} but got {dbConnection.GetType().Name}. " +
                $"{nameof(PostgreSqlBlobStore<TDbContext>)} requires a PostgreSQL database.");

        if (connection.State != ConnectionState.Open) {
            await connection.OpenAsync(cancellationToken);
        }

        return connection;
    }

    private NpgsqlTransaction? GetCurrentNpgsqlTransaction() {
        var transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        if (transaction is null) {
            return null;
        }

        return transaction as NpgsqlTransaction
            ?? throw new InvalidOperationException(
                $"Expected {nameof(NpgsqlTransaction)} but got {transaction.GetType().Name}. " +
                $"{nameof(PostgreSqlBlobStore<TDbContext>)} requires a PostgreSQL database.");
    }

    private static BlobMetadata ToMetadata(StoredBlob blob) =>
        new() {
            Id = blob.Id,
            Container = blob.Container,
            Name = blob.Name,
            ContentType = blob.ContentType,
            Size = blob.Size,
            CreatedAt = blob.CreatedAt,
            State = blob.State,
            OwnerId = blob.OwnerId
        };

    private static void ValidateUploadRequest(BlobUploadRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Container, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name, nameof(request));
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ContentType, nameof(request));
    }

    private static void ValidateBlobRef(BlobRef blobRef) {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobRef.Value, nameof(blobRef));
    }
}

/// <summary>
/// Materialization shape for the listing's entry query (<c>SqlQueryRaw</c> needs a non-generic type
/// with settable properties matching the column aliases).
/// </summary>
internal sealed class BlobListEntryRow {
    public string Entry { get; set; } = string.Empty;

    public bool IsPrefix { get; set; }
}
