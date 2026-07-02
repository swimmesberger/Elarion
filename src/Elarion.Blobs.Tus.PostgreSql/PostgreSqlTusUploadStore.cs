using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;

namespace Elarion.Blobs.Tus.PostgreSql;

/// <summary>
/// Durable PostgreSQL <see cref="ITusUploadStore"/>: stages the in-progress bytes in a <c>bytea</c> column
/// so an upload survives a restart and can be resumed by any instance.
/// </summary>
/// <typeparam name="TDbContext">The EF Core context whose model includes <see cref="TusUploadRow"/> via <c>UseElarionTusStorage</c>.</typeparam>
/// <remarks>
/// Each chunk is appended with one conditional <c>UPDATE … SET data = data || …</c> guarded on the current
/// offset, so a stale or concurrent <c>PATCH</c> is rejected as an offset conflict. The staged bytes are
/// buffered in memory per chunk and again at finalization; for the very large uploads this matters, a
/// future optimization is large-object or chunk-row storage.
/// </remarks>
public sealed class PostgreSqlTusUploadStore<TDbContext>(
    TDbContext dbContext,
    IBlobStore blobStore,
    TusOptions options,
    TusGcOptions gcOptions,
    TimeProvider timeProvider) : ITusUploadStore
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async Task<TusUpload> CreateAsync(TusUploadCreation creation, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(creation);

        var now = timeProvider.GetUtcNow();
        var row = new TusUploadRow {
            Id = Guid.NewGuid().ToString("N"),
            Container = creation.Container,
            Name = creation.Name,
            UploadLength = creation.Length,
            UploadOffset = 0,
            ContentType = creation.ContentType,
            Metadata = creation.Metadata,
            OwnerId = creation.OwnerId,
            ExpiresAt = now + options.UploadExpiry,
            CreatedAt = now,
            BlobId = null,
            Data = [],
        };

        dbContext.Set<TusUploadRow>().Add(row);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Map(row, row.UploadOffset, row.BlobId);
    }

    /// <inheritdoc />
    public async Task<TusUpload?> GetAsync(string uploadId, CancellationToken cancellationToken) {
        var row = await dbContext.Set<TusUploadRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == uploadId, cancellationToken);

        return row is null ? null : Map(row, row.UploadOffset, row.BlobId);
    }

    /// <inheritdoc />
    public async Task<TusUpload> AppendAsync(
        string uploadId,
        long offset,
        Stream chunk,
        CancellationToken cancellationToken) {
        var row = await dbContext.Set<TusUploadRow>()
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == uploadId, cancellationToken);

        if (row is null || row.BlobId is not null || offset != row.UploadOffset) {
            throw new TusOffsetConflictException();
        }

        var bytes = await ReadAtMostAsync(chunk, row.UploadLength - row.UploadOffset, cancellationToken);
        var length = bytes.Length;

        // Conditional append: only advances a row still at the expected offset and not yet finalized, so a
        // concurrent or replayed PATCH affects zero rows and is reported as an offset conflict.
        var sql = AppendSqlCache.GetOrAdd(dbContext.Model, static (_, ctx) => BuildAppendSql(ctx), dbContext);
        var affected = await dbContext.Database.ExecuteSqlRawAsync(
            sql,
            [bytes, length, uploadId, offset],
            cancellationToken);

        if (affected == 0) {
            throw new TusOffsetConflictException();
        }

        var newOffset = offset + length;
        return newOffset >= row.UploadLength
            ? await FinalizeAsync(uploadId, cancellationToken)
            : Map(row, newOffset, blobId: null);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string uploadId, CancellationToken cancellationToken) =>
        await dbContext.Set<TusUploadRow>()
            .Where(r => r.Id == uploadId)
            .ExecuteDeleteAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<int> DeleteExpiredAsync(DateTimeOffset olderThanUtc, int batchSize, CancellationToken cancellationToken) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        // Reap both kinds of expired session by their eligibility instant: an incomplete session past its
        // upload-expiry window (the "abort incomplete multipart upload" analog), and a completed session
        // past its completed-retention window (finalization stamps ExpiresAt = now + CompletedRetention),
        // so completed rows do not grow the staging table unbounded. A completed row still lives long
        // enough for a HEAD to fetch its Elarion-Blob-Ref.
        var ids = await dbContext.Set<TusUploadRow>()
            .AsNoTracking()
            .Where(r => r.ExpiresAt < olderThanUtc)
            .OrderBy(r => r.ExpiresAt)
            .Take(batchSize)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

        if (ids.Count == 0) {
            return 0;
        }

        return await dbContext.Set<TusUploadRow>()
            .Where(r => ids.Contains(r.Id) && r.ExpiresAt < olderThanUtc)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<TusUpload> FinalizeAsync(string uploadId, CancellationToken cancellationToken) {
        var row = await dbContext.Set<TusUploadRow>()
            .AsNoTracking()
            .FirstAsync(r => r.Id == uploadId, cancellationToken);

        if (row.BlobId is not null) {
            return Map(row, row.UploadOffset, row.BlobId);
        }

        // Write the pending blob and stamp the session's blob_id atomically: a crash between the two would
        // otherwise leave a completed session (offset == length) with no blob reference — the client would
        // see a finished upload that never yields Elarion-Blob-Ref, and the garbage collector would later
        // delete it silently. A single transaction makes both land or neither. The blob store shares this
        // DbContext, so its own SaveAsync joins this ambient transaction instead of committing on its own.
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try {
            var blobRef = await blobStore.SaveAsync(
                new BlobUploadRequest {
                    Container = row.Container,
                    Name = row.Name,
                    ContentType = row.ContentType,
                    ContentLength = row.UploadLength,
                    InitialState = BlobLifecycleState.Pending,
                    ExpiresAt = timeProvider.GetUtcNow() + options.Ttl,
                    OwnerId = row.OwnerId,
                },
                new MemoryStream(row.Data, writable: false),
                cancellationToken);

            // Mark complete, drop the staged bytes (the content now lives in the blob), and record when the
            // completed session becomes eligible for reclamation so it lives long enough for a HEAD.
            var completedExpiresAt = timeProvider.GetUtcNow() + gcOptions.CompletedRetention;
            await dbContext.Set<TusUploadRow>()
                .Where(r => r.Id == uploadId && r.BlobId == null)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(r => r.BlobId, blobRef.Value)
                        .SetProperty(r => r.Data, Array.Empty<byte>())
                        .SetProperty(r => r.ExpiresAt, completedExpiresAt),
                    cancellationToken);

            if (transaction is not null) {
                await transaction.CommitAsync(cancellationToken);
            }

            // The scan above is AsNoTracking, so mutating the local row only shapes the returned snapshot.
            row.ExpiresAt = completedExpiresAt;
            return Map(row, row.UploadLength, blobRef.Value);
        }
        finally {
            if (transaction is not null) {
                await transaction.DisposeAsync();
            }
        }
    }

    // The append SQL is built from the EF model (not hard-coded identifiers) so the UseElarionTusStorage
    // table/schema overrides and the snake-case toggle apply to the raw conditional-append path too. Built
    // once per model and reused.
    private static readonly ConcurrentDictionary<IModel, string> AppendSqlCache = new();

    private static string BuildAppendSql(DbContext context) {
        var entityType = context.Model.FindEntityType(typeof(TusUploadRow))
            ?? throw new InvalidOperationException(
                "The tus upload entity is not mapped. Call modelBuilder.UseElarionTusStorage() in OnModelCreating.");
        var sqlHelper = context.GetService<ISqlGenerationHelper>();

        var tableName = entityType.GetTableName()
            ?? throw new InvalidOperationException("The tus upload entity is not mapped to a table.");
        var schema = entityType.GetSchema();
        var storeObject = StoreObjectIdentifier.Table(tableName, schema);

        string Column(string propertyName) {
            var property = entityType.FindProperty(propertyName)
                ?? throw new InvalidOperationException($"The {nameof(TusUploadRow)}.{propertyName} property is not mapped.");
            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException($"The {nameof(TusUploadRow)}.{propertyName} property has no column.");
            return sqlHelper.DelimitIdentifier(columnName);
        }

        var table = sqlHelper.DelimitIdentifier(tableName, schema);
        var data = Column(nameof(TusUploadRow.Data));
        var uploadOffset = Column(nameof(TusUploadRow.UploadOffset));
        var id = Column(nameof(TusUploadRow.Id));
        var blobId = Column(nameof(TusUploadRow.BlobId));

        return $"UPDATE {table} SET {data} = {data} || {{0}}, {uploadOffset} = {uploadOffset} + {{1}} " +
            $"WHERE {id} = {{2}} AND {uploadOffset} = {{3}} AND {blobId} IS NULL";
    }

    private static TusUpload Map(TusUploadRow row, long offset, string? blobId) =>
        new() {
            Id = row.Id,
            Length = row.UploadLength,
            Offset = offset,
            ContentType = row.ContentType,
            Metadata = row.Metadata,
            OwnerId = row.OwnerId,
            ExpiresAt = row.ExpiresAt,
            BlobRef = blobId is null ? null : new BlobRef { Value = blobId },
        };

    private static async Task<byte[]> ReadAtMostAsync(Stream source, long max, CancellationToken cancellationToken) {
        if (max <= 0) {
            return [];
        }

        using var buffer = new MemoryStream();
        var rent = new byte[81920];
        long total = 0;
        while (total < max) {
            var toRead = (int)Math.Min(rent.Length, max - total);
            var read = await source.ReadAsync(rent.AsMemory(0, toRead), cancellationToken);
            if (read == 0) {
                break;
            }

            buffer.Write(rent, 0, read);
            total += read;
        }

        return buffer.ToArray();
    }
}
