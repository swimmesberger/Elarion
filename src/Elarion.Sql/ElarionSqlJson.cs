using Elarion.Abstractions.Serialization;

namespace Elarion.Sql;

/// <summary>
/// The canonical JSON accessor (ADR-0023) that a <see cref="SqlJsonAttribute">[SqlJson]</see> mapper's
/// static <c>Instance</c> serializes through — the one seam that a self-mapping JSON row needs, since a
/// <c>static abstract</c> mapper property cannot take a constructor dependency.
/// </summary>
/// <remarks>
/// Install it once at startup — <c>ElarionSqlJson.Use(app.Services.GetRequiredService&lt;IElarionJsonSerialization&gt;())</c>
/// — before any JSON-column mapper is used. This is deliberately explicit rather than auto-installed:
/// it keeps <c>Elarion.Sql</c> free of a hosting dependency and keeps the install point visible. A
/// JSON-free host never touches this. Setting a different instance twice fails loud; the same instance
/// twice is a no-op (idempotent).
/// </remarks>
public static class ElarionSqlJson {
    private static IElarionJsonSerialization? _serialization;

    /// <summary>Installs the canonical JSON accessor used by <c>[SqlJson]</c> mappers' static instances.</summary>
    public static void Use(IElarionJsonSerialization serialization) {
        ArgumentNullException.ThrowIfNull(serialization);
        var existing = Interlocked.CompareExchange(ref _serialization, serialization, null);
        if (existing is not null && !ReferenceEquals(existing, serialization)) {
            throw new InvalidOperationException(
                "ElarionSqlJson.Use was already called with a different IElarionJsonSerialization instance. "
                + "Install the canonical accessor exactly once per process.");
        }
    }

    /// <summary>The installed accessor; used by generated JSON-mapper <c>Instance</c> members.</summary>
    public static IElarionJsonSerialization Serialization =>
        _serialization ?? throw new InvalidOperationException(
            "A [SqlJson] column mapper needs the canonical JSON accessor, which has not been installed. "
            + "Call ElarionSqlJson.Use(serialization) once at startup "
            + "(e.g. ElarionSqlJson.Use(app.Services.GetRequiredService<IElarionJsonSerialization>())) "
            + "before using JSON-column mappers, or construct the mapper explicitly with its "
            + "IElarionJsonSerialization constructor.");
}
