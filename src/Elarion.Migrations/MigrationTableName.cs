namespace Elarion.Migrations;

/// <summary>Shared validation for the history table name across providers.</summary>
internal static class MigrationTableName {
    /// <summary>History table names are plain identifiers; anything else fails before touching the database.</summary>
    public static void Validate(string tableName) {
        var valid = tableName.Length > 0
            && (char.IsAsciiLetter(tableName[0]) || tableName[0] == '_')
            && tableName.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        if (!valid) {
            throw new MigrationException(
                $"History table name '{tableName}' is not a plain identifier (letters, digits, underscores, not starting with a digit).");
        }
    }
}
