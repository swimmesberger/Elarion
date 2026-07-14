namespace Elarion.Sql;

/// <summary>The database provider the SQL mapper generator emits provider-specific code for.</summary>
public enum SqlProvider {
    /// <summary>Provider-neutral emission (plain ADO.NET, values inferred by the provider).</summary>
    Portable = 0,

    /// <summary>
    /// PostgreSQL via Npgsql: <see cref="SqlJsonAttribute">[SqlJson]</see> parameters bind as
    /// <c>NpgsqlDbType.Jsonb</c> (a plain string parameter would fail PostgreSQL's jsonb type check).
    /// The consuming assembly must reference Npgsql.
    /// </summary>
    Npgsql = 1,
}

/// <summary>
/// Assembly-level provider trigger for the SQL mapper generator (precedent: the keyset emitter under
/// <c>[UseElarionEntityFrameworkCore(Provider = …)]</c>). Without it, emission stays provider-neutral.
/// </summary>
/// <example>
/// <code>
/// [assembly: Elarion.Sql.UseElarionSql(Provider = Elarion.Sql.SqlProvider.Npgsql)]
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class UseElarionSqlAttribute : Attribute {
    /// <summary>The provider to emit for; defaults to <see cref="SqlProvider.Portable"/>.</summary>
    public SqlProvider Provider { get; set; }
}
