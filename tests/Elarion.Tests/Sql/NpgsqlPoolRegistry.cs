using System.Collections.Concurrent;
using System.Reflection;
using Npgsql;

namespace Elarion.Tests.SqlMapping;

/// <summary>
/// Reads Npgsql's internal static pool registry (<c>Npgsql.PoolManager</c>), where
/// <c>new NpgsqlConnection(connectionString)</c> registers one data source per distinct connection string for
/// the life of the process. Tests run in parallel and other tests do register entries, so callers mark their
/// connection strings with a unique <c>Application Name</c> and ask about that marker instead of comparing
/// counts.
/// </summary>
internal static class NpgsqlPoolRegistry {
    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> Pools = FindPools();

    /// <summary>Whether any registered pool's connection string contains <paramref name="marker"/>.</summary>
    public static bool Contains(string marker) {
        return Pools.Keys.Any(key => key.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>A connection string to the same database, tagged with a unique, searchable application name.</summary>
    public static string Mark(string connectionString, string marker) {
        return new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = marker }.ConnectionString;
    }

    private static ConcurrentDictionary<string, NpgsqlDataSource> FindPools() {
        var poolManager = typeof(NpgsqlConnection).Assembly.GetType("Npgsql.PoolManager", throwOnError: true)!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        // Field or auto-property backing field, whichever this Npgsql version uses; found by type, not name.
        var value = poolManager.GetFields(flags)
            .Where(f => f.FieldType == typeof(ConcurrentDictionary<string, NpgsqlDataSource>))
            .Select(f => f.GetValue(null))
            .Concat(poolManager.GetProperties(flags)
                .Where(p => p.PropertyType == typeof(ConcurrentDictionary<string, NpgsqlDataSource>))
                .Select(p => p.GetValue(null)))
            .FirstOrDefault(v => v is not null);
        return value as ConcurrentDictionary<string, NpgsqlDataSource>
               ?? throw new InvalidOperationException(
                   "Npgsql.PoolManager no longer keeps a ConcurrentDictionary<string, NpgsqlDataSource>; update this probe.");
    }
}
