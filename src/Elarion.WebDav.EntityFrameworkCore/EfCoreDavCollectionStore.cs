using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace Elarion.WebDav.EntityFrameworkCore;

/// <summary>
/// The EF-backed <see cref="IDavCollectionStore"/> over <typeparamref name="TDbContext"/>, whose model maps the store
/// through <c>[GenerateElarionDavStore]</c> or <c>UseElarionDavStore</c>. PostgreSQL only.
/// </summary>
/// <remarks>
/// <para>
/// Scoped, over the request's own context: a write joins the context's current transaction (the handler's unit of
/// work), so a published object becomes visible exactly when the handler commits and disappears with a rollback (spec
/// 0006 FR-005); without one, the write runs in a transaction of its own, as <c>Elarion.Blobs.PostgreSql</c> does. The
/// statements run on the context's connection, never a second one.
/// </para>
/// <para>
/// A publish first compares the stored entity tag without a lock and writes nothing when it is equal (FR-006). Every
/// other change takes the collection's row lock (<c>SELECT … FOR UPDATE</c>), so concurrent publishers of one collection
/// on any node serialise and their counters strictly increase, appends one journal row per changed member, and prunes the
/// collection's journal rows beyond <see cref="WebDavOptions.MaxJournalRowsPerCollection"/> or older than
/// <see cref="WebDavOptions.JournalRetention"/>, raising the low-water mark in the same transaction (FR-021). Statements
/// are built from the EF model, so the table and column names follow <c>UseElarionDavStore</c>.
/// </para>
/// </remarks>
public sealed class EfCoreDavCollectionStore<TDbContext>(TDbContext dbContext, IOptions<WebDavOptions> options, TimeProvider timeProvider)
    : IDavCollectionStore where TDbContext : DbContext {
    private const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    // Weak-keyed on the model, so a process that builds many models (a test suite) does not keep them alive.
    private static readonly ConditionalWeakTable<IModel, DavStoreSql> SqlCache = new();

    private DavStoreSql Sql => SqlCache.GetValue(dbContext.Model, _ => DavStoreSql.Create(dbContext));

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<DavCollectionState>> GetStatesAsync(IReadOnlyList<DavCollectionAddress> addresses,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(addresses);
        if (addresses.Count == 0) return [];
        var found = new Dictionary<Guid, (long Seq, long LowWater)>();
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            await using var command = Command(Sql.SelectStates, ("ids", addresses.Select(static a => a.Id).Distinct().ToArray()));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                found[reader.GetGuid(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        }

        return [
            .. addresses.Select(address => found.TryGetValue(address.Id, out var state)
                ? new DavCollectionState(address, state.Seq, state.LowWater)
                : new DavCollectionState(address, 0, 0)),
        ];
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DavMember> ListMembersAsync(Guid collectionId, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            await using var command = Command(Sql.ListMembers, ("c", collectionId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                yield return new DavMember { Name = reader.GetString(0), ETag = reader.GetString(1), ContentType = reader.GetString(2), Size = reader.GetInt64(3) };
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DavStoredObject> GetObjectsAsync(Guid collectionId, IReadOnlyCollection<string> names, bool includeContent,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0) yield break;
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            await using var command = Command(includeContent ? Sql.GetObjectsWithContent : Sql.GetObjects,
                ("c", collectionId), ("names", names.Distinct(StringComparer.Ordinal).ToArray()));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) yield return ReadObject(reader, includeContent);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<DavStoredObject> GetCandidatesAsync(Guid collectionId, DateTimeOffset? start, DateTimeOffset? end,
        [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            await using var command = Command(Sql.GetCandidates, ("c", collectionId), ("s", start?.ToUniversalTime()), ("e", end?.ToUniversalTime()));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) yield return ReadObject(reader, true);
        }
    }

    /// <inheritdoc />
    public async ValueTask<DavChangeSet?> GetChangesAsync(Guid collectionId, long sinceSeq, CancellationToken cancellationToken = default) {
        // One statement, so the counter, the low-water mark and the journal rows come from one snapshot: the rows answered
        // end exactly at the counter answered.
        long? current = null;
        long lowWater = 0;
        var changes = new List<DavChange>();
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            await using var command = Command(Sql.GetChanges, ("c", collectionId), ("since", sinceSeq));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) {
                current = reader.GetInt64(0);
                lowWater = reader.GetInt64(1);
                if (!reader.IsDBNull(2)) changes.Add(new DavChange(reader.GetString(3), (DavChangeKind)reader.GetInt16(4), reader.GetInt64(2)));
            }
        }

        if (current is null) return sinceSeq == 0 ? new DavChangeSet(0, []) : null;
        if (sinceSeq < lowWater || sinceSeq > current) return null;
        return new DavChangeSet(current.Value, changes);
    }

    /// <inheritdoc />
    public async ValueTask<DavPublishOutcome> PublishAsync(DavCollectionAddress address, DavObjectContent content,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(content);
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            // Equal bytes write nothing, without a lock: re-publishing after every save costs no client a refetch (FR-006).
            if (await GetETagAsync(address.Id, content.Name, cancellationToken).ConfigureAwait(false) == content.ETag) return DavPublishOutcome.Unchanged;

            await using var transaction = await BeginAsync(cancellationToken).ConfigureAwait(false);
            var seq = await LockAsync(address, cancellationToken).ConfigureAwait(false);
            var existing = await GetETagAsync(address.Id, content.Name, cancellationToken).ConfigureAwait(false);
            if (existing == content.ETag) {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return DavPublishOutcome.Unchanged;
            }

            var kind = existing is null ? DavChangeKind.Added : DavChangeKind.Changed;
            await UpsertAsync(address.Id, content, cancellationToken).ConfigureAwait(false);
            await RecordAsync(address.Id, [(content.Name, kind)], seq, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return kind == DavChangeKind.Added ? DavPublishOutcome.Added : DavPublishOutcome.Changed;
        }
    }

    /// <inheritdoc />
    public async ValueTask<bool> RemoveAsync(DavCollectionAddress address, string name, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrEmpty(name);
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            if (await GetETagAsync(address.Id, name, cancellationToken).ConfigureAwait(false) is null) return false;

            await using var transaction = await BeginAsync(cancellationToken).ConfigureAwait(false);
            var seq = await LockAsync(address, cancellationToken).ConfigureAwait(false);
            await using var delete = Command(Sql.DeleteObjects, ("c", address.Id), ("names", new[] { name }));
            var deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (deleted > 0) await RecordAsync(address.Id, [(name, DavChangeKind.Removed)], seq, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted > 0;
        }
    }

    /// <inheritdoc />
    public async ValueTask<DavReplaceOutcome> ReplaceAsync(DavCollectionAddress address, IAsyncEnumerable<DavObjectContent> contents,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(contents);
        await using (await OpenAsync(cancellationToken).ConfigureAwait(false)) {
            await using var transaction = await BeginAsync(cancellationToken).ConfigureAwait(false);
            var seq = await LockAsync(address, cancellationToken).ConfigureAwait(false);

            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var list = Command(Sql.ListMembers, ("c", address.Id))) {
                await using var reader = await list.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) stored[reader.GetString(0)] = reader.GetString(1);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var changes = new List<(string Name, DavChangeKind Kind)>();
            int added = 0, changed = 0;
            await foreach (var content in contents.WithCancellation(cancellationToken).ConfigureAwait(false)) {
                seen.Add(content.Name);
                if (stored.TryGetValue(content.Name, out var etag) && etag == content.ETag) continue;
                var kind = etag is null ? DavChangeKind.Added : DavChangeKind.Changed;
                await UpsertAsync(address.Id, content, cancellationToken).ConfigureAwait(false);
                stored[content.Name] = content.ETag;
                changes.RemoveAll(change => change.Name == content.Name);
                changes.Add((content.Name, kind));
            }

            foreach (var (_, kind) in changes)
                if (kind == DavChangeKind.Added) added++;
                else changed++;

            var gone = stored.Keys.Where(name => !seen.Contains(name)).Order(StringComparer.Ordinal).ToArray();
            if (gone.Length > 0) {
                await using var delete = Command(Sql.DeleteObjects, ("c", address.Id), ("names", gone));
                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                changes.AddRange(gone.Select(static name => (name, DavChangeKind.Removed)));
            }

            if (changes.Count > 0) await RecordAsync(address.Id, changes, seq, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DavReplaceOutcome(added, changed, gone.Length);
        }
    }

    private async ValueTask<string?> GetETagAsync(Guid collectionId, string name, CancellationToken cancellationToken) {
        await using var command = Command(Sql.GetETag, ("c", collectionId), ("n", name));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    // Creates the collection row when it is missing, then takes its row lock and returns its counter. The lock is held to
    // the end of the transaction, so the counter read here is the one this writer increments.
    private async ValueTask<long> LockAsync(DavCollectionAddress address, CancellationToken cancellationToken) {
        await using (var insert = Command(Sql.InsertCollection, ("id", address.Id), ("kind", address.Kind), ("source", address.Source),
                         ("key", address.Key)))
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using var select = Command(Sql.LockCollection, ("id", address.Id));
        return (long)(await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private async ValueTask UpsertAsync(Guid collectionId, DavObjectContent content, CancellationToken cancellationToken) {
        await using var command = Command(Sql.UpsertObject, ("c", collectionId), ("n", content.Name), ("uid", content.Uid), ("etag", content.ETag),
            ("content", content.Content.ToArray()), ("ct", content.ContentType), ("rs", content.RangeStart?.ToUniversalTime()),
            ("re", content.RangeEnd?.ToUniversalTime()), ("size", (long)content.Content.Length));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // One counter value per change, in order, so a sync answer can be cut after any change; then the journal is pruned and
    // the low-water mark raised in the same transaction.
    private async ValueTask RecordAsync(Guid collectionId, IReadOnlyList<(string Name, DavChangeKind Kind)> changes, long seq,
        CancellationToken cancellationToken) {
        var now = timeProvider.GetUtcNow();
        foreach (var (name, kind) in changes) {
            seq++;
            await using var insert = Command(Sql.InsertChange, ("c", collectionId), ("seq", seq), ("n", name), ("op", (short)kind), ("at", now));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var settings = options.Value;
        await using var prune = Command(Sql.PruneAndAdvance, ("c", collectionId), ("seq", seq),
            ("keepAbove", seq - settings.MaxJournalRowsPerCollection), ("cutoff", now - settings.JournalRetention));
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DavStoredObject ReadObject(DbDataReader reader, bool includeContent) => new() {
        Name = reader.GetString(0),
        ETag = reader.GetString(1),
        ContentType = reader.GetString(2),
        Size = reader.GetInt64(3),
        Uid = reader.GetString(4),
        Content = includeContent ? (byte[])reader.GetValue(5) : ReadOnlyMemory<byte>.Empty,
    };

    private async ValueTask<ConnectionLease> OpenAsync(CancellationToken cancellationToken) {
        if (!string.Equals(dbContext.Database.ProviderName, NpgsqlProvider, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{nameof(EfCoreDavCollectionStore<TDbContext>)} requires PostgreSQL (Npgsql); the context uses '{dbContext.Database.ProviderName}'.");
        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionLease(dbContext);
    }

    // Joins the context's transaction (the caller's unit of work) or opens one of its own.
    private async ValueTask<TransactionLease> BeginAsync(CancellationToken cancellationToken) {
        if (dbContext.Database.CurrentTransaction is not null) return new TransactionLease(null);
        return new TransactionLease(await dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
    }

    private DbCommand Command(string sql, params (string Name, object? Value)[] parameters) {
        var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        foreach (var (name, value) in parameters) {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            if (value is null) parameter.DbType = DbType.DateTimeOffset;
            command.Parameters.Add(parameter);
        }

        return command;
    }

    private sealed class ConnectionLease(DbContext context) : IAsyncDisposable {
        public async ValueTask DisposeAsync() => await context.Database.CloseConnectionAsync().ConfigureAwait(false);
    }

    private sealed class TransactionLease(IDbContextTransaction? owned) : IAsyncDisposable {
        public Task CommitAsync(CancellationToken cancellationToken) => owned?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

        public async ValueTask DisposeAsync() {
            if (owned is not null) await owned.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>The store's statements, built once per model from the mapped table and column names.</summary>
internal sealed class DavStoreSql {
    public required string SelectStates { get; init; }
    public required string ListMembers { get; init; }
    public required string GetObjects { get; init; }
    public required string GetObjectsWithContent { get; init; }
    public required string GetCandidates { get; init; }
    public required string GetChanges { get; init; }
    public required string GetETag { get; init; }
    public required string InsertCollection { get; init; }
    public required string LockCollection { get; init; }
    public required string UpsertObject { get; init; }
    public required string DeleteObjects { get; init; }
    public required string InsertChange { get; init; }
    public required string PruneAndAdvance { get; init; }

    public static DavStoreSql Create(DbContext context) {
        var helper = context.GetService<ISqlGenerationHelper>();
        var model = context.Model;
        var (collections, c) = Table<DavCollectionEntity>(model, helper);
        var (objects, o) = Table<DavObjectEntity>(model, helper);
        var (changes, ch) = Table<DavChangeEntity>(model, helper);

        string Cc(string property) => c(property);
        string Oc(string property) => o(property);
        string Chc(string property) => ch(property);

        var memberColumns = $"{Oc("Name")}, {Oc("ETag")}, {Oc("ContentType")}, {Oc("Size")}, {Oc("Uid")}";
        return new DavStoreSql {
            SelectStates = $"SELECT {Cc("Id")}, {Cc("SyncSeq")}, {Cc("LowWater")} FROM {collections} WHERE {Cc("Id")} = ANY(@ids)",
            ListMembers = $"SELECT {Oc("Name")}, {Oc("ETag")}, {Oc("ContentType")}, {Oc("Size")} FROM {objects} WHERE {Oc("CollectionId")} = @c ORDER BY {Oc("Name")}",
            GetObjects = $"SELECT {memberColumns} FROM {objects} WHERE {Oc("CollectionId")} = @c AND {Oc("Name")} = ANY(@names) ORDER BY {Oc("Name")}",
            GetObjectsWithContent =
                $"SELECT {memberColumns}, {Oc("Content")} FROM {objects} WHERE {Oc("CollectionId")} = @c AND {Oc("Name")} = ANY(@names) ORDER BY {Oc("Name")}",
            GetCandidates =
                $"SELECT {memberColumns}, {Oc("Content")} FROM {objects} WHERE {Oc("CollectionId")} = @c AND ({Oc("RangeStart")} IS NULL OR "
                + $"((@e IS NULL OR {Oc("RangeStart")} <= @e) AND (@s IS NULL OR {Oc("RangeEnd")} IS NULL OR {Oc("RangeEnd")} >= @s))) ORDER BY {Oc("Name")}",
            GetChanges =
                $"SELECT col.{Cc("SyncSeq")}, col.{Cc("LowWater")}, chg.{Chc("Seq")}, chg.{Chc("Name")}, chg.{Chc("Op")} FROM {collections} col "
                + $"LEFT JOIN {changes} chg ON chg.{Chc("CollectionId")} = col.{Cc("Id")} AND chg.{Chc("Seq")} > @since AND chg.{Chc("Seq")} <= col.{Cc("SyncSeq")} "
                + $"WHERE col.{Cc("Id")} = @c ORDER BY chg.{Chc("Seq")}",
            GetETag = $"SELECT {Oc("ETag")} FROM {objects} WHERE {Oc("CollectionId")} = @c AND {Oc("Name")} = @n",
            InsertCollection =
                $"INSERT INTO {collections} ({Cc("Id")}, {Cc("Kind")}, {Cc("Source")}, {Cc("Key")}, {Cc("SyncSeq")}, {Cc("LowWater")}) "
                + "VALUES (@id, @kind, @source, @key, 0, 0) ON CONFLICT DO NOTHING",
            LockCollection = $"SELECT {Cc("SyncSeq")} FROM {collections} WHERE {Cc("Id")} = @id FOR UPDATE",
            UpsertObject =
                $"INSERT INTO {objects} ({Oc("CollectionId")}, {Oc("Name")}, {Oc("Uid")}, {Oc("ETag")}, {Oc("Content")}, {Oc("ContentType")}, "
                + $"{Oc("RangeStart")}, {Oc("RangeEnd")}, {Oc("Size")}) VALUES (@c, @n, @uid, @etag, @content, @ct, @rs, @re, @size) "
                + $"ON CONFLICT ({Oc("CollectionId")}, {Oc("Name")}) DO UPDATE SET {Oc("Uid")} = EXCLUDED.{Oc("Uid")}, {Oc("ETag")} = EXCLUDED.{Oc("ETag")}, "
                + $"{Oc("Content")} = EXCLUDED.{Oc("Content")}, {Oc("ContentType")} = EXCLUDED.{Oc("ContentType")}, "
                + $"{Oc("RangeStart")} = EXCLUDED.{Oc("RangeStart")}, {Oc("RangeEnd")} = EXCLUDED.{Oc("RangeEnd")}, {Oc("Size")} = EXCLUDED.{Oc("Size")}",
            DeleteObjects = $"DELETE FROM {objects} WHERE {Oc("CollectionId")} = @c AND {Oc("Name")} = ANY(@names)",
            InsertChange =
                $"INSERT INTO {changes} ({Chc("CollectionId")}, {Chc("Seq")}, {Chc("Name")}, {Chc("Op")}, {Chc("ChangedAt")}) VALUES (@c, @seq, @n, @op, @at)",
            PruneAndAdvance =
                $"WITH pruned AS (DELETE FROM {changes} WHERE {Chc("CollectionId")} = @c AND ({Chc("Seq")} <= @keepAbove OR {Chc("ChangedAt")} < @cutoff) "
                + $"RETURNING {Chc("Seq")}) UPDATE {collections} SET {Cc("SyncSeq")} = @seq, "
                + $"{Cc("LowWater")} = GREATEST({Cc("LowWater")}, COALESCE((SELECT MAX({Chc("Seq")}) FROM pruned), 0)) WHERE {Cc("Id")} = @c",
        };
    }

    private static (string Table, Func<string, string> Column) Table<TEntity>(IModel model, ISqlGenerationHelper helper) {
        var entity = model.FindEntityType(typeof(TEntity)) ?? throw new InvalidOperationException(
            $"The context does not map {typeof(TEntity).Name}; annotate it with [GenerateElarionDavStore] or call modelBuilder.UseElarionDavStore().");
        var tableName = entity.GetTableName()!;
        var schema = entity.GetSchema();
        var store = StoreObjectIdentifier.Table(tableName, schema);
        return (helper.DelimitIdentifier(tableName, schema),
            property => helper.DelimitIdentifier(entity.FindProperty(property)!.GetColumnName(store)!));
    }
}
