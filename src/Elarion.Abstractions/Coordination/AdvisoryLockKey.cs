using System.ComponentModel;

namespace Elarion.Abstractions.Coordination;

/// <summary>
/// The one key scheme for PostgreSQL advisory locks taken through Elarion: a lock on <c>(scope, key)</c> locks the
/// 64-bit value <c>hashtextextended('scope:key', 0)</c>. Every Elarion package and every application using the
/// <c>AdvisoryLockAsync</c> helpers hashes the same way, so two different <c>(scope, key)</c> pairs only collide when
/// their hashes do — and the framework's scopes are reserved, so an application key never shares text with one.
/// </summary>
/// <remarks>
/// <para>
/// A database has one advisory-lock space, shared by session-level and transaction-level locks. The framework uses it
/// for the scopes in <see cref="Scopes"/> (all under <see cref="ReservedScopePrefix"/>) and, at session level, for the
/// migration runner's fixed key (<c>PostgreSqlMigrationRunner.DefaultAdvisoryLockKey</c>, the first eight bytes of
/// SHA-256 of a constant — not a <c>hashtextextended</c> value). An application scope must not start with
/// <see cref="ReservedScopePrefix"/>.
/// </para>
/// <para>
/// A host on the EF-free SQL tier builds the same text with <see cref="Text"/> and runs
/// <c>SELECT pg_advisory_xact_lock(hashtextextended($1, 0))</c> itself.
/// </para>
/// </remarks>
public static class AdvisoryLockKey {
    /// <summary>The prefix of every scope the framework itself locks; reserved for it.</summary>
    public const string ReservedScopePrefix = "elarion.";

    /// <summary>The separator between scope and key in the hashed text.</summary>
    public const char Separator = ':';

    /// <summary>The scopes the framework locks, so an application can see that none of its keys share their text.</summary>
    public static class Scopes {
        /// <summary>One scheduled job's occurrence claim (key: the job name).</summary>
        public const string SchedulerJob = "elarion.scheduler.job";

        /// <summary>Reissuing one device's pairing code (key: the device id).</summary>
        public const string DevicePairing = "elarion.devices.pairing";
    }

    /// <summary>
    /// The text an application lock on <c>(<paramref name="scope"/>, <paramref name="key"/>)</c> hashes:
    /// <c>scope:key</c>. Rejects an empty scope, a scope containing <see cref="Separator"/> (two pairs would
    /// otherwise share a text) and a reserved scope.
    /// </summary>
    /// <exception cref="ArgumentException">The scope is empty, contains the separator, or is reserved.</exception>
    public static string Text(string scope, string key) {
        if (scope is not null && scope.StartsWith(ReservedScopePrefix, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Advisory lock scopes starting with '{ReservedScopePrefix}' are reserved for the framework.",
                nameof(scope));

        return FrameworkText(scope!, key);
    }

    /// <summary>
    /// The text of a lock in one of the framework's <see cref="Scopes"/>. For Elarion packages; an application uses
    /// <see cref="Text"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The scope is empty or contains the separator.</exception>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static string FrameworkText(string scope, string key) {
        ArgumentException.ThrowIfNullOrEmpty(scope);
        ArgumentNullException.ThrowIfNull(key);
        if (scope.Contains(Separator, StringComparison.Ordinal))
            throw new ArgumentException($"An advisory lock scope must not contain '{Separator}'.", nameof(scope));

        return string.Concat(scope, ":", key);
    }
}
