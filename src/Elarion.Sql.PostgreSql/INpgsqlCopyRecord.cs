using Npgsql;

namespace Elarion.Sql;

/// <summary>
/// The binary-COPY capability a <see cref="SqlRecordAttribute">[SqlRecord]</see> type gains when the
/// assembly opts into the Npgsql provider (<c>[assembly: UseElarionSql(Provider = SqlProvider.Npgsql)]</c>)
/// and references <c>Elarion.Sql.PostgreSql</c> (ADR-0068). The generated partial implements every member,
/// so the bulk extensions resolve the COPY plan from the type argument alone — the same
/// static-abstract self-mapping shape as <see cref="ISqlRecord{TSelf}"/>, compile-time and reflection-free.
/// </summary>
/// <remarks>
/// The interface is deliberately Npgsql-specific and lives in the provider package: bulk ingestion is a
/// per-provider capability, not a portable seam. An assembly compiled with the portable provider does not
/// implement it, so calling the bulk extensions on such a record is a missing-constraint compile error,
/// never a runtime probe. Column writes use the same CLR-type inference as the generated
/// <c>BindParameters</c> (explicit <c>NpgsqlDbType</c> only where the parameter path is explicit, e.g.
/// <c>jsonb</c>), so a row that inserts through <c>InsertManyAsync</c> COPYs with identical semantics.
/// </remarks>
public interface INpgsqlCopyRecord<TSelf> where TSelf : INpgsqlCopyRecord<TSelf> {
    /// <summary>
    /// The full binary-COPY command over all mapped columns:
    /// <c>COPY {table} ({columns}) FROM STDIN (FORMAT BINARY)</c>.
    /// </summary>
    static abstract string CopyCommandText { get; }

    /// <summary>The mapped table name, for composing the staged-upsert statements.</summary>
    static abstract string CopyTableName { get; }

    /// <summary>All mapped column names, comma-separated, in COPY write order.</summary>
    static abstract string CopyColumnList { get; }

    /// <summary>
    /// Starts a row on the importer and writes every mapped column in <see cref="CopyColumnList"/> order.
    /// </summary>
    static abstract Task WriteRowAsync(NpgsqlBinaryImporter importer, TSelf row, CancellationToken cancellationToken);
}
