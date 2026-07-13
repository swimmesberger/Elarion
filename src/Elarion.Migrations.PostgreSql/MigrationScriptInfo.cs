namespace Elarion.Migrations.PostgreSql;

/// <summary>Describes one discovered migration script.</summary>
public sealed record MigrationScriptInfo {
    /// <summary>The script file name, e.g. <c>V20260713093000__add_devices.sql</c>.</summary>
    public required string ScriptName { get; init; }

    /// <summary>The version in canonical dotted form (e.g. <c>1.2</c>), or <see langword="null"/> for a repeatable script.</summary>
    public string? Version { get; init; }

    /// <summary>The description parsed from the file name, with underscores as spaces.</summary>
    public required string Description { get; init; }

    /// <summary>The SHA-256 checksum (lowercase hex) of the BOM-stripped, CRLF→LF-normalized script content.</summary>
    public required string Checksum { get; init; }

    /// <summary>Whether this is a repeatable (<c>R__</c>) script.</summary>
    public bool IsRepeatable => Version is null;
}
