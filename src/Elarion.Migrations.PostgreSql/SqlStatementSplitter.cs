namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// Splits a <c>-- elarion: no-transaction</c> script into individual statements so each executes as its
/// own implicit transaction — a multi-statement command travels as one simple-query message, which
/// PostgreSQL wraps in a single implicit transaction and which therefore breaks
/// <c>CREATE INDEX CONCURRENTLY</c>, the very statement the directive exists for. Understands line and
/// nested block comments, single-/double-quoted and <c>E''</c> strings, and dollar quoting. Transactional
/// scripts are never split; they run as one command inside their explicit transaction.
/// </summary>
internal static class SqlStatementSplitter {
    public static List<string> Split(string sql) {
        var statements = new List<string>();
        var start = 0;
        var significant = false;
        var i = 0;
        var n = sql.Length;

        while (i < n) {
            var c = sql[i];

            if (c == '-' && i + 1 < n && sql[i + 1] == '-') {
                var newline = sql.IndexOf('\n', i + 2);
                i = newline < 0 ? n : newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < n && sql[i + 1] == '*') {
                // PostgreSQL block comments nest.
                var depth = 1;
                i += 2;
                while (i < n && depth > 0) {
                    if (sql[i] == '/' && i + 1 < n && sql[i + 1] == '*') {
                        depth++;
                        i += 2;
                    }
                    else if (sql[i] == '*' && i + 1 < n && sql[i + 1] == '/') {
                        depth--;
                        i += 2;
                    }
                    else {
                        i++;
                    }
                }

                continue;
            }

            if (c == '\'') {
                var escapeString = IsEscapeStringPrefix(sql, i);
                significant = true;
                i++;
                while (i < n) {
                    if (escapeString && sql[i] == '\\' && i + 1 < n) {
                        i += 2;
                        continue;
                    }

                    if (sql[i] == '\'') {
                        if (i + 1 < n && sql[i + 1] == '\'') {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '"') {
                significant = true;
                i++;
                while (i < n) {
                    if (sql[i] == '"') {
                        if (i + 1 < n && sql[i + 1] == '"') {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '$' && TryReadDollarTag(sql, i, out var tag)) {
                significant = true;
                var close = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal);
                i = close < 0 ? n : close + tag.Length;
                continue;
            }

            if (c == ';') {
                if (significant) {
                    statements.Add(sql[start..i].Trim());
                }

                i++;
                start = i;
                significant = false;
                continue;
            }

            if (!char.IsWhiteSpace(c)) {
                significant = true;
            }

            i++;
        }

        if (significant) {
            var tail = sql[start..].Trim();
            if (tail.Length > 0) {
                statements.Add(tail);
            }
        }

        return statements;
    }

    /// <summary>An <c>E'…'</c>/<c>e'…'</c> string uses backslash escapes; the E must not itself end an identifier.</summary>
    private static bool IsEscapeStringPrefix(string sql, int quoteIndex) {
        if (quoteIndex == 0) {
            return false;
        }

        var previous = sql[quoteIndex - 1];
        if (previous is not ('E' or 'e')) {
            return false;
        }

        if (quoteIndex == 1) {
            return true;
        }

        var beforePrevious = sql[quoteIndex - 2];
        return !char.IsLetterOrDigit(beforePrevious) && beforePrevious != '_';
    }

    /// <summary>Reads a <c>$tag$</c> opener at <paramref name="index"/>: <c>$$</c> or <c>$identifier$</c>.</summary>
    private static bool TryReadDollarTag(string sql, int index, out string tag) {
        tag = "";
        var i = index + 1;
        while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) {
            // A tag must not start with a digit ($1 is a parameter, not a quote).
            if (i == index + 1 && char.IsDigit(sql[i])) {
                return false;
            }

            i++;
        }

        if (i >= sql.Length || sql[i] != '$') {
            return false;
        }

        tag = sql[index..(i + 1)];
        return true;
    }
}
