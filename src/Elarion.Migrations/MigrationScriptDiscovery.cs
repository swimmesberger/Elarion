using System.Reflection;
using System.Text;

namespace Elarion.Migrations;

/// <summary>
/// Reads migration scripts from assembly manifest resources (AOT-safe, no filesystem). Validation is
/// fail-closed and total: within the configured scope every <c>.sql</c> resource must parse — nothing is
/// silently skipped — and all problems are collected, not just the first.
/// </summary>
internal static class MigrationScriptDiscovery {
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static MigrationStepSet Discover(IReadOnlyList<MigrationScriptSource> sources) {
        var errors = new List<MigrationValidationError>();
        var scripts = new List<MigrationStep>();
        var seenResources = new HashSet<(Assembly Assembly, string ResourceName)>();

        foreach (var source in sources) {
            var names = source.Assembly.GetManifestResourceNames()
                .Where(name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
                               && (source.ResourceNamePrefix is null ||
                                   name.StartsWith(source.ResourceNamePrefix, StringComparison.Ordinal)))
                .Order(StringComparer.Ordinal);

            foreach (var resourceName in names) {
                if (!seenResources.Add((source.Assembly, resourceName))) continue;

                var script = TryRead(source.Assembly, resourceName, errors);
                if (script is not null) scripts.Add(script);
            }
        }

        return new MigrationStepSet { Steps = scripts, Errors = errors };
    }

    private static ScriptMigrationStep? TryRead(Assembly assembly, string resourceName,
        List<MigrationValidationError> errors) {
        var scriptName = ExtractFileName(resourceName);
        if (!TryParseScriptName(scriptName, out var version, out var description, out var nameError)) {
            errors.Add(new MigrationValidationError { StepName = resourceName, Message = nameError });
            return null;
        }

        string content;
        using (var stream = assembly.GetManifestResourceStream(resourceName)!)
        using (var memory = new MemoryStream()) {
            stream.CopyTo(memory);
            try {
                content = StrictUtf8.GetString(memory.GetBuffer().AsSpan(0, (int)memory.Length));
            }
            catch (DecoderFallbackException) {
                errors.Add(new MigrationValidationError {
                    StepName = resourceName,
                    Message = $"Migration script resource '{resourceName}' is not valid UTF-8."
                });
                return null;
            }
        }

        var normalized = MigrationChecksum.Normalize(content);
        if (!TryParseDirectives(normalized, out var noTransaction, out var directiveError)) {
            errors.Add(new MigrationValidationError
                { StepName = resourceName, Message = $"Migration script '{resourceName}': {directiveError}" });
            return null;
        }

        return new ScriptMigrationStep {
            ResourceName = resourceName,
            ScriptName = scriptName,
            ParsedVersion = version,
            ScriptDescription = description,
            Sql = normalized,
            ContentChecksum = MigrationChecksum.Compute(normalized),
            NoTransaction = noTransaction
        };
    }

    /// <summary>
    /// The script file name is the resource name's last dot-segment plus the <c>.sql</c> suffix — folder
    /// paths become dot-separated namespaces in manifest resource names. Consequence: version segments in
    /// file names are separated by single underscores (<c>V1_2__desc.sql</c> for version 1.2), never dots.
    /// </summary>
    internal static string ExtractFileName(string resourceName) {
        var stem = resourceName[..^".sql".Length];
        var lastDot = stem.LastIndexOf('.');
        return (lastDot < 0 ? stem : stem[(lastDot + 1)..]) + ".sql";
    }

    internal static bool TryParseScriptName(string scriptName, out MigrationVersion? version, out string description,
        out string error) {
        version = null;
        description = "";
        error = "";
        var stem = scriptName[..^".sql".Length];

        if (stem.StartsWith("R__", StringComparison.Ordinal)) {
            var descriptionPart = stem["R__".Length..];
            if (descriptionPart.Length == 0) {
                error = $"Migration script '{scriptName}' has an empty description; expected 'R__{{description}}.sql'.";
                return false;
            }

            description = descriptionPart.Replace('_', ' ');
            return true;
        }

        if (stem.StartsWith('V')) {
            var separator = stem.IndexOf("__", StringComparison.Ordinal);
            if (separator <= 1 || separator + 2 >= stem.Length) {
                error = $"Migration script '{scriptName}' does not match 'V{{version}}__{{description}}.sql'.";
                return false;
            }

            var versionPart = stem[1..separator];
            if (!MigrationVersion.TryParse(versionPart, out version)) {
                error =
                    $"Migration script '{scriptName}' has an invalid version '{versionPart}'; expected numeric segments separated by '_' (e.g. 'V20260713093000__…' or 'V1_2__…').";
                return false;
            }

            description = stem[(separator + 2)..].Replace('_', ' ');
            return true;
        }

        error =
            $"Migration script '{scriptName}' does not match 'V{{version}}__{{description}}.sql' or 'R__{{description}}.sql'. "
            + "Every .sql resource in a configured script source must be a migration script (fail-closed; nothing is skipped).";
        return false;
    }

    /// <summary>
    /// Scans the leading comment block (before the first SQL line) for <c>-- elarion: {directive}</c>
    /// lines. An unknown directive is an error — a typo silently ignored would change transaction
    /// semantics without anyone noticing.
    /// </summary>
    internal static bool TryParseDirectives(string normalizedSql, out bool noTransaction, out string error) {
        noTransaction = false;
        error = "";

        foreach (var rawLine in normalizedSql.Split('\n')) {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            if (!line.StartsWith("--", StringComparison.Ordinal)) break;

            var comment = line[2..].Trim();
            if (!comment.StartsWith("elarion:", StringComparison.OrdinalIgnoreCase)) continue;

            var directive = comment["elarion:".Length..].Trim();
            if (string.Equals(directive, "no-transaction", StringComparison.OrdinalIgnoreCase)) {
                noTransaction = true;
            }
            else {
                error = $"unknown directive '-- elarion: {directive}'; supported: 'no-transaction'.";
                return false;
            }
        }

        return true;
    }
}
