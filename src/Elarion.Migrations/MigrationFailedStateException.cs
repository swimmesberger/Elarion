namespace Elarion.Migrations;

/// <summary>
/// The history contains an unresolved failed migration (a <c>-- elarion: no-transaction</c> script that
/// failed half-applied), so the run fails closed. Resolve it deliberately with
/// <see cref="IMigrationRunner.ResolveFailedAsync"/> — <see cref="ResolveAction.Retry"/> after fixing the
/// script, or <see cref="ResolveAction.MarkApplied"/> after completing the change by hand.
/// </summary>
public sealed class MigrationFailedStateException : MigrationException {
    /// <summary>Creates the exception for the given failed migration.</summary>
    public MigrationFailedStateException(string version, string scriptName, string message) : base(message) {
        Version = version;
        ScriptName = scriptName;
    }

    /// <summary>The version of the failed migration — the argument for <see cref="IMigrationRunner.ResolveFailedAsync"/>.</summary>
    public string Version { get; }

    /// <summary>The file name of the failed script.</summary>
    public string ScriptName { get; }
}
