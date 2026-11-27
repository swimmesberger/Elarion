namespace Elarion.Contacts.Writing;

/// <summary>What the writer does to every value before it is written (FR-082).</summary>
internal static class ContactText {
    /// <summary>
    /// <paramref name="value"/> without control characters other than the tab, the line feed and the carriage return:
    /// U+0000–U+001F and U+007F–U+009F. A value has no line break by then (escaped, or rejected as a URI); a parameter
    /// value's line break is caret-encoded by the line writer. A value without one is returned as it is.
    /// </summary>
    public static string WithoutControls(string value) {
        var first = IndexOfControl(value);
        if (first < 0) return value;
        return string.Create(value.Length - CountControls(value, first), (value, first), static (span, state) => {
            var (source, from) = state;
            source.AsSpan(0, from).CopyTo(span);
            var at = from;
            for (var i = from; i < source.Length; i++)
                if (!IsDropped(source[i])) span[at++] = source[i];
        });
    }

    /// <summary>The values without control characters; the same list when none has one.</summary>
    public static IReadOnlyList<string> WithoutControls(IReadOnlyList<string> values) {
        for (var i = 0; i < values.Count; i++) {
            if (IndexOfControl(values[i]) < 0) continue;
            var cleaned = new string[values.Count];
            for (var j = 0; j < cleaned.Length; j++) cleaned[j] = WithoutControls(values[j]);
            return cleaned;
        }

        return values;
    }

    private static bool IsDropped(char c) => c is (< ' ' and not ('\t' or '\n' or '\r')) or (>= '\u007F' and <= '\u009F');

    private static int IndexOfControl(string value) {
        for (var i = 0; i < value.Length; i++)
            if (IsDropped(value[i])) return i;
        return -1;
    }

    private static int CountControls(string value, int from) {
        var count = 0;
        for (var i = from; i < value.Length; i++)
            if (IsDropped(value[i])) count++;
        return count;
    }
}
