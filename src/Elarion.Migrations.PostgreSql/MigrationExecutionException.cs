namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// A script failed while executing. For a transactional script the transaction — including its history
/// row — rolled back, so fixing the script and rerunning is the whole recovery. For a
/// <c>-- elarion: no-transaction</c> script a failed history row was recorded and subsequent runs fail
/// closed until <see cref="IMigrationRunner.ResolveFailedAsync"/> resolves it.
/// </summary>
public sealed class MigrationExecutionException : MigrationException {
    /// <summary>Creates the exception for the given script.</summary>
    public MigrationExecutionException(string scriptName, string message, Exception innerException)
        : base(message, innerException) {
        ScriptName = scriptName;
    }

    /// <summary>The file name of the script that failed.</summary>
    public string ScriptName { get; }
}
