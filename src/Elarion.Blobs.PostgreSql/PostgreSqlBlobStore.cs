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
public sealed class PostgreSqlBlobStore<TDbContext>(
    TDbContext dbContext,
    ILogger<PostgreSqlBlobStore<TDbContext>> logger,
    TimeProvider timeProvider) : IBlobStore
    where TDbContext : DbContext {
    /// <inheritdoc />
    public async Task<BlobRef> SaveAsync(
        string container,
        string name,
        string contentType,
        byte[] content,
        CancellationToken cancellationToken) {
        ValidateBlobIdentity(container, name, contentType);
        ArgumentNullException.ThrowIfNull(content);

        var blobId = await SaveBlobAsync(
            container,
            name,
            contentType,
            content.Length,
            blobId => UpsertContentBytesAsync(blobId, content, cancellationToken),
            cancellationToken);

        logger.LogInformation(
            "Blob stored: {BlobId} ({Container}/{Name}, {Size} bytes)",
            blobId,
            container,
            name,
            content.Length);

        return new BlobRef { Value = blobId };
    }

    /// <inheritdoc />
    public async Task<BlobRef> SaveFromFileAsync(
        string container,
        string name,
        string contentType,
        string filePath,
        CancellationToken cancellationToken) {
        ValidateBlobIdentity(container, name, contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) {
            throw new FileNotFoundException($"Source file not found: {filePath}", filePath);
        }

        var blobId = await SaveBlobAsync(
            container,
            name,
            contentType,
            fileInfo.Length,
            blobId => UpsertContentFileAsync(blobId, fileInfo, cancellationToken),
            cancellationToken);

        logger.LogInformation(
            "Blob stored from file: {BlobId} ({Container}/{Name}, {Size} bytes, source: {FilePath})",
            blobId,
            container,
            name,
            fileInfo.Length,
            filePath);

        return new BlobRef { Value = blobId };
    }

    /// <inheritdoc />
    public async Task<BlobContent?> GetAsync(BlobRef blobRef, CancellationToken cancellationToken) {
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

        return new BlobContent {
            Id = metadata.Id,
            Container = metadata.Container,
            Name = metadata.Name,
            ContentType = metadata.ContentType,
            Size = metadata.Size,
            CreatedAt = metadata.CreatedAt,
            Data = data
        };
    }

    /// <inheritdoc />
    public async Task<BlobMetadata?> GetMetadataAsync(BlobRef blobRef, CancellationToken cancellationToken) {
        ValidateBlobRef(blobRef);

        var blob = await dbContext.Set<StoredBlob>()
            .AsNoTracking()
            .FirstOrDefaultAsync(storedBlob => storedBlob.Id == blobRef.Value, cancellationToken);

        if (blob is null) {
            return null;
        }

        return new BlobMetadata {
            Id = blob.Id,
            Container = blob.Container,
            Name = blob.Name,
            ContentType = blob.ContentType,
            Size = blob.Size,
            CreatedAt = blob.CreatedAt
        };
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
        string container,
        string name,
        string contentType,
        long size,
        Func<string, Task> saveContent,
        CancellationToken cancellationToken) {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var blobId = await UpsertMetadataAsync(container, name, contentType, size, cancellationToken);
        await saveContent(blobId);

        if (transaction is not null) {
            await transaction.CommitAsync(cancellationToken);
        }

        return blobId;
    }

    private async Task<string> UpsertMetadataAsync(
        string container,
        string name,
        string contentType,
        long size,
        CancellationToken cancellationToken) {
        var existing = await dbContext.Set<StoredBlob>()
            .FirstOrDefaultAsync(blob => blob.Container == container && blob.Name == name, cancellationToken);
        var createdAt = timeProvider.GetUtcNow();

        if (existing is not null) {
            existing.ContentType = contentType;
            existing.Size = size;
            existing.CreatedAt = createdAt;

            logger.LogDebug("Updating existing blob {BlobId} ({Container}/{Name})", existing.Id, container, name);
            await dbContext.SaveChangesAsync(cancellationToken);

            return existing.Id;
        }

        var blobId = Guid.NewGuid().ToString();
        dbContext.Set<StoredBlob>().Add(new StoredBlob {
            Id = blobId,
            Container = container,
            Name = name,
            ContentType = contentType,
            Size = size,
            CreatedAt = createdAt,
        });

        logger.LogDebug("Creating new blob {BlobId} ({Container}/{Name})", blobId, container, name);
        await dbContext.SaveChangesAsync(cancellationToken);

        return blobId;
    }

    private async Task UpsertContentBytesAsync(
        string blobId,
        byte[] content,
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

    private async Task UpsertContentFileAsync(
        string blobId,
        FileInfo fileInfo,
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

        await using var fileStream = new FileStream(
            fileInfo.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        command.Parameters.Add(new NpgsqlParameter { Value = fileStream, NpgsqlDbType = NpgsqlDbType.Bytea });

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

    private static void ValidateBlobIdentity(string container, string name, string contentType) {
        ArgumentException.ThrowIfNullOrWhiteSpace(container);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
    }

    private static void ValidateBlobRef(BlobRef blobRef) {
        ArgumentException.ThrowIfNullOrWhiteSpace(blobRef.Value, nameof(blobRef));
    }
}
