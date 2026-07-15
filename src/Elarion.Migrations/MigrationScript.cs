namespace Elarion.Migrations;

/// <summary>One discovered, parsed, and checksummed migration script.</summary>
internal sealed record MigrationScript {
    /// <summary>The full manifest resource name the script came from.</summary>
    public required string ResourceName { get; init; }

    /// <summary>The script file name (the resource name's last segment), e.g. <c>V1__init.sql</c>.</summary>
    public required string ScriptName { get; init; }

    /// <summary>The parsed version, or <see langword="null"/> for a repeatable script.</summary>
    public required MigrationVersion? Version { get; init; }

    /// <summary>The description parsed from the file name, underscores as spaces.</summary>
    public required string Description { get; init; }

    /// <summary>The normalized (BOM-stripped, CRLF→LF) script content.</summary>
    public required string Sql { get; init; }

    /// <summary>SHA-256 (lowercase hex) of the normalized content.</summary>
    public required string Checksum { get; init; }

    /// <summary>Whether the script carries the <c>-- elarion: no-transaction</c> directive.</summary>
    public required bool NoTransaction { get; init; }

    public bool IsRepeatable => Version is null;

    public MigrationScriptInfo ToInfo() => new() {
        ScriptName = ScriptName,
        Version = Version?.Text,
        Description = Description,
        Checksum = Checksum,
    };
}
