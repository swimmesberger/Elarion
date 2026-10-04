namespace Elarion.Migrations;

/// <summary>One discovered, parsed, and checksummed embedded SQL script, as a plan step.</summary>
internal sealed class ScriptMigrationStep : MigrationStep {
    /// <summary>The full manifest resource name the script came from.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The script file name (the resource name's last segment), e.g. <c>V1__init.sql</c>.</summary>
    public required string ScriptName { get; init; }

    /// <summary>The normalized (BOM-stripped, CRLF→LF) script content.</summary>
    public required string Sql { get; init; }

    /// <summary>SHA-256 (lowercase hex) of the normalized content.</summary>
    public required string ContentChecksum { get; init; }

    /// <summary>Whether the script carries the <c>-- elarion: no-transaction</c> directive.</summary>
    public required bool NoTransaction { get; init; }

    /// <summary>The parsed version, or <see langword="null"/> for a repeatable script.</summary>
    public required MigrationVersion? ParsedVersion { get; init; }

    public required string ScriptDescription { get; init; }

    public override string Kind => MigrationStepKinds.Sql;

    public override string Name => ScriptName;

    public override string? Version => ParsedVersion?.Text;

    public override string Description => ScriptDescription;

    public override string Origin => ResourceName;

    public override string? Checksum => ContentChecksum;

    public override bool UseTransaction => !NoTransaction;

    // A half-applied schema needs a human: a failed versioned no-transaction script fails closed. A repeatable
    // is idempotent by doctrine and its changed checksum was never recorded, so it simply retries.
    public override bool RecordsFailedRow => ParsedVersion is not null;

    public override Task ExecuteAsync(MigrationStepContext context, CancellationToken cancellationToken) {
        return context.ExecuteSqlAsync(Sql, cancellationToken);
    }
}

/// <summary>The embedded-SQL-script step source (<see cref="MigrationOptions.AddScripts"/>).</summary>
internal sealed class ScriptStepSource(IReadOnlyList<MigrationScriptSource> sources) : IMigrationStepSource {
    public MigrationStepSet Discover(IServiceProvider services) {
        return MigrationScriptDiscovery.Discover(sources);
    }
}
