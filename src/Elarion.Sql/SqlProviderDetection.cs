using System.Data.Common;

namespace Elarion.Sql;

/// <summary>
/// Structural provider detection for the provider-neutral SQL tier. The tier takes no driver dependency, so a
/// provider-specific capability is recognised by the ADO.NET type's namespace rather than by a type test.
/// </summary>
internal static class SqlProviderDetection {
    /// <summary>Whether <paramref name="providerObject"/> (a connection or command) belongs to Npgsql.</summary>
    internal static bool IsNpgsql(object providerObject) {
        return providerObject.GetType().FullName?.StartsWith("Npgsql.", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Fails before a <see cref="SqlArray"/> parameter reaches a provider that cannot bind it. The check is an
    /// allow-list (PostgreSQL only) so an unknown provider fails here with an actionable message instead of
    /// deep inside the driver — or, worse, binding the array as something else.
    /// </summary>
    internal static void ThrowIfArrayParametersUnsupported(DbCommand command) {
        if (IsNpgsql(command)) return;

        throw new NotSupportedException(
            $"This statement binds an array parameter (SqlArray), but the ADO.NET provider behind "
            + $"'{command.GetType().FullName}' has no array parameters (SqlArray is supported on PostgreSQL through "
            + "Npgsql). Interpolate the collection directly to expand it into an IN list instead: 'WHERE id IN {ids}'.");
    }
}
