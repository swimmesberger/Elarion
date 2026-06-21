using System.Data;
using Elarion.Blobs;
using Microsoft.EntityFrameworkCore;
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
/// truthful); without a hint it is buffered first only to learn its length. Reads are buffered into
/// memory for now; the
/// streaming upgrade path is <see cref="CommandBehavior.SequentialAccess"/> plus
/// <c>NpgsqlDataReader.GetStream("data")</c> on a dedicated connection, attached to the returned
/// <see cref="BlobDownload"/> as its owned resource so the reader and connection live exactly as
/// long as the caller reads.
/// </remarks>
public sealed class PostgreSqlBlobStore<TDbContext>(
    TDbContext dbContext,
    ILogger<PostgreSqlBlobStore<TDbContext>> logger,
    TimeProvider timeProvider) : IBlobStore
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

        var connection = await GetOpenNpgsqlConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = GetCurrentNpgsqlTransaction();
        command.CommandText = "SELECT data FROM blob_contents WHERE blob_id = $1";
        command.Parameters.Add(new NpgsqlParameter { Value = blobRef.Value });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }

        var data = (byte[])reader["data"];

        // Buffered for now: the content is fully materialized, so the MemoryStream owns no
        // backend resources and BlobDownload has nothing extra to dispose.
        return new BlobDownload(ToMetadata(metadata), new MemoryStream(data, writable: false));
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

        if (existing is not null) {
            existing.ContentType = request.ContentType;
            existing.Size = size;
            existing.CreatedAt = createdAt;

            logger.LogDebug(
                "Updating existing blob {BlobId} ({Container}/{Name})",
                existing.Id,
                request.Container,
                request.Name);
            await dbContext.SaveChangesAsync(cancellationToken);

            return existing.Id;
        }

        var blobId = Guid.NewGuid().ToString();
        dbContext.Set<StoredBlob>().Add(new StoredBlob {
            Id = blobId,
            Container = request.Container,
            Name = request.Name,
            ContentType = request.ContentType,
            Size = size,
            CreatedAt = createdAt,
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
        command.CommandText = """
            INSERT INTO blob_contents (blob_id, data)
            VALUES ($1, $2)
            ON CONFLICT (blob_id) DO UPDATE SET data = EXCLUDED.data
            """;
        command.Parameters.Add(new NpgsqlParameter { Value = blobId });
        command.Parameters.Add(new NpgsqlParameter { Value = content, NpgsqlDbType = NpgsqlDbType.Bytea });

        await command.ExecuteNonQueryAsync(cancellationToken);
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
            CreatedAt = blob.CreatedAt
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
