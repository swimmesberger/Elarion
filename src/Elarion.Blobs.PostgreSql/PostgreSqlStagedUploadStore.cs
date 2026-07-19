using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.Blobs.PostgreSql;

/// <summary>
/// Durable PostgreSQL <see cref="IStagedUploadStore"/>: stages the in-progress bytes in a <c>bytea</c>
/// column so an upload survives a restart and can be resumed by any instance.
/// </summary>
/// <typeparam name="TDbContext">The EF Core context whose model includes <see cref="StagedUploadRow"/> via <c>UseElarionStagedUploads</c>.</typeparam>
/// <remarks>
/// Each chunk is appended with one conditional <c>UPDATE … SET data = data || …</c> guarded on the current
/// offset, so a stale or concurrent append is rejected as a conflict. The staged bytes are buffered in
/// memory per chunk and again at completion; for the very large uploads this matters, a future
/// optimization is large-object or chunk-row storage.
/// </remarks>
public sealed class PostgreSqlStagedUploadStore<TDbContext>(
    TDbContext dbContext,
    IBlobStore blobStore,
    TimeProvider timeProvider) : IStagedUploadStore
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async Task<StagedUpload> CreateAsync(StagedUploadCreation creation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(creation);

        var row = new StagedUploadRow {
            Id = Guid.CreateVersion7().ToString("N"),
            Container = creation.Container,
            Name = creation.Name,
            Length = creation.Length,
            Offset = 0,
            ContentType = creation.ContentType,
            Metadata = creation.Metadata,
            OwnerId = creation.OwnerId,
            ExpiresAt = creation.ExpiresAt,
            CreatedAt = timeProvider.GetUtcNow(),
            BlobId = null,
            Data = []
        };

        dbContext.Set<StagedUploadRow>().Add(row);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(row);
    }

    /// <inheritdoc />
    public async Task<StagedUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) {
        var row = await QueryHeader(uploadId).FirstOrDefaultAsync(cancellationToken);

        return row is null ? null : Map(row);
    }

    /// <inheritdoc />
    public async Task<StagedUpload> AppendAsync(
        string uploadId,
        long offset,
        Stream chunk,
        CancellationToken cancellationToken) {
        var row = await QueryHeader(uploadId).FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.BlobId is not null || offset != row.Offset)
            throw new StagedUploadConflictException(
                $"Upload session '{uploadId}' does not exist, is complete, or is not at offset {offset}.");

        // A declared length caps the read to the remaining bytes; a deferred length reads the caller's
        // whole (caller-bounded) chunk.
        var remaining = row.Length is long declared ? declared - row.Offset : long.MaxValue;
        var bytes = await ReadAtMostAsync(chunk, remaining, cancellationToken);
        var length = bytes.Length;

        // Conditional append: only advances a row still at the expected offset and not yet completed, so a
        // concurrent or replayed append affects zero rows and is reported as a conflict.
        var sql = AppendSqlCache.GetOrAdd(dbContext.Model, static (_, ctx) => BuildAppendSql(ctx), dbContext);
        var affected = await dbContext.Database.ExecuteSqlRawAsync(
            sql,
            [bytes, length, uploadId, offset],
            cancellationToken);

        if (affected == 0)
            throw new StagedUploadConflictException(
                $"Upload session '{uploadId}' is no longer at offset {offset}.");

        return Map(row) with { Offset = offset + length };
    }

    /// <inheritdoc />
    public async Task<StagedUpload> CompleteAsync(
        string uploadId,
        StagedUploadCompletion completion,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(completion);

        // Write the pending blob and stamp the session's blob_id atomically: a crash between the two would
        // otherwise leave a finished session with no blob reference — the client would see a completed
        // upload that never yields its reference, and the garbage collector would later delete it silently.
        // A single transaction makes both land or neither. The blob store shares this DbContext, so its own
        // SaveAsync joins this ambient transaction instead of committing on its own.
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try {
            var row = await dbContext.Set<StagedUploadRow>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == uploadId, cancellationToken);

            if (row is null) throw new StagedUploadConflictException($"Upload session '{uploadId}' does not exist.");

            if (row.BlobId is not null) return Map(row);

            if (row.Length is long declared && row.Offset != declared)
                throw new StagedUploadConflictException(
                    $"Upload session '{uploadId}' declares {declared} bytes but received {row.Offset}.");

            var blobRef = await blobStore.SaveAsync(
                new BlobUploadRequest {
                    Container = row.Container,
                    Name = row.Name,
                    ContentType = row.ContentType,
                    ContentLength = row.Offset,
                    InitialState = BlobLifecycleState.Pending,
                    ExpiresAt = completion.BlobExpiresAt,
                    OwnerId = row.OwnerId
                },
                new MemoryStream(row.Data, false),
                cancellationToken);

            // Mark complete, seal a deferred length at the received byte count, drop the staged bytes (the
            // content now lives in the blob), and stamp the completed-retention deadline. Guarded on the
            // loaded offset so a concurrent append between the read and this stamp surfaces as a conflict
            // instead of silently discarding its bytes.
            var affected = await dbContext.Set<StagedUploadRow>()
                .Where(r => r.Id == uploadId && r.BlobId == null && r.Offset == row.Offset)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.BlobId, blobRef.Value)
                        .SetProperty(r => r.Length, row.Offset)
                        .SetProperty(r => r.Data, Array.Empty<byte>())
                        .SetProperty(r => r.ExpiresAt, completion.SessionExpiresAt),
                    cancellationToken);

            if (affected == 0)
                // A concurrent completion or append won the race. Rolling back (by not committing the owned
                // transaction) also discards the duplicate blob write above; under a caller-owned ambient
                // transaction the duplicate pending blob is reclaimed by garbage collection.
                throw new StagedUploadConflictException(
                    $"Upload session '{uploadId}' changed concurrently while completing.");

            if (transaction is not null) await transaction.CommitAsync(cancellationToken);

            // The scan above is AsNoTracking, so mutating the local row only shapes the returned snapshot.
            row.Length = row.Offset;
            row.ExpiresAt = completion.SessionExpiresAt;
            row.BlobId = blobRef.Value;
            return Map(row);
        }
        finally {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string uploadId, CancellationToken cancellationToken) {
        await dbContext.Set<StagedUploadRow>()
            .Where(r => r.Id == uploadId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize,
        CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Reap both kinds of expired session by their eligibility instant: an incomplete session past its
        // upload-expiry window (the "abort incomplete multipart upload" analog), and a completed session
        // past its completed-retention window (completion stamps ExpiresAt), so completed rows do not grow
        // the staging table unbounded. A completed row still lives long enough for a status probe to fetch
        // its blob reference.
        var ids = await dbContext.Set<StagedUploadRow>()
            .AsNoTracking()
            .Where(r => r.ExpiresAt < olderThanUtc)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0) return 0;

        return await dbContext.Set<StagedUploadRow>()
            .Where(r => ids.Contains(r.Id) && r.ExpiresAt < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    // The append SQL is built from the EF model (not hard-coded identifiers) so the UseElarionStagedUploads
    // table/schema overrides and the snake-case toggle apply to the raw conditional-append path too. Built
    // once per model and reused.
    private static readonly ConcurrentDictionary<IModel, string> AppendSqlCache = new();

    private static string BuildAppendSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(StagedUploadRow))
                         ?? throw new InvalidOperationException(
                             "The staged-upload entity is not mapped. Call modelBuilder.UseElarionStagedUploads() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
                        ?? throw new InvalidOperationException("The staged-upload entity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                           ?? throw new InvalidOperationException(
                               $"The {nameof(StagedUploadRow)}.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                             ?? throw new InvalidOperationException(
                                 $"The {nameof(StagedUploadRow)}.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var data = Column(nameof(StagedUploadRow.Data));
        var offset = Column(nameof(StagedUploadRow.Offset));
        var id = Column(nameof(StagedUploadRow.Id));
        var blobId = Column(nameof(StagedUploadRow.BlobId));

        return $"UPDATE {table} SET {data} = {data} || {{0}}, {offset} = {offset} + {{1}} " +
               $"WHERE {id} = {{2}} AND {offset} = {{3}} AND {blobId} IS NULL";
    }

    // Status probes and the append pre-check read only the session header — never the staged bytea —
    // so a HEAD/PATCH on a large in-progress upload does not re-transfer everything received so far.
    // Only CompleteAsync materializes Data.
    private IQueryable<StagedUploadHeader> QueryHeader(string uploadId) {
        return dbContext.Set<StagedUploadRow>()
            .AsNoTracking()
            .Where(r => r.Id == uploadId)
            .Select(r => new StagedUploadHeader(
                r.Id, r.Length, r.Offset, r.ContentType, r.Metadata, r.OwnerId, r.ExpiresAt, r.BlobId));
    }

    /// <summary>The non-content columns of a staging row: everything a status probe or append pre-check needs.</summary>
    private sealed record StagedUploadHeader(
        string Id,
        long? Length,
        long Offset,
        string ContentType,
        string? Metadata,
        string? OwnerId,
        DateTimeOffset ExpiresAt,
        string? BlobId);

    private static StagedUpload Map(StagedUploadRow row) {
        return new StagedUpload {
            Id = row.Id,
            Length = row.Length,
            Offset = row.Offset,
            ContentType = row.ContentType,
            Metadata = row.Metadata,
            OwnerId = row.OwnerId,
            ExpiresAt = row.ExpiresAt,
            BlobRef = row.BlobId is null ? null : new BlobRef { Value = row.BlobId }
        };
    }

    private static StagedUpload Map(StagedUploadHeader row) {
        return new StagedUpload {
            Id = row.Id,
            Length = row.Length,
            Offset = row.Offset,
            ContentType = row.ContentType,
            Metadata = row.Metadata,
            OwnerId = row.OwnerId,
            ExpiresAt = row.ExpiresAt,
            BlobRef = row.BlobId is null ? null : new BlobRef { Value = row.BlobId }
        };
    }

    private static async Task<byte[]> ReadAtMostAsync(Stream source, long max, CancellationToken cancellationToken) {
        if (max <= 0) return [];

        using var buffer = new MemoryStream();
        var rent = new byte[81920];
        long total = 0;
        while (total < max) {
            var toRead = (int)Math.Min(rent.Length, max - total);
            var read = await source.ReadAsync(rent.AsMemory(0, toRead), cancellationToken);
            if (read == 0) break;

            buffer.Write(rent, 0, read);
            total += read;
        }

        return buffer.ToArray();
    }
}
