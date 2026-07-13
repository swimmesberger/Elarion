using System.Diagnostics.CodeAnalysis;

namespace Elarion.Migrations.PostgreSql;

/// <summary>
/// A migration version: dot- or underscore-separated numeric segments (<c>1</c>, <c>1.2</c>,
/// <c>20260713093000</c>). Compared segment-wise with missing segments as zero, so <c>1</c> equals
/// <c>1.0</c>. <see cref="Text"/> is the canonical dotted rendering used in the history table.
/// </summary>
internal sealed class MigrationVersion : IComparable<MigrationVersion>, IEquatable<MigrationVersion> {
    private readonly long[] _segments;

    private MigrationVersion(long[] segments) {
        _segments = segments;
        Text = string.Join('.', segments);
    }

    /// <summary>The canonical dotted form, e.g. <c>1.2</c>.</summary>
    public string Text { get; }

    /// <summary>Parses a version string; segments separated by <c>.</c> or <c>_</c>, each a non-negative integer.</summary>
    public static bool TryParse(string value, [NotNullWhen(true)] out MigrationVersion? version) {
        version = null;
        if (string.IsNullOrEmpty(value)) {
            return false;
        }

        var parts = value.Split('.', '_');
        var segments = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++) {
            if (parts[i].Length == 0 || !long.TryParse(parts[i], out segments[i]) || segments[i] < 0) {
                return false;
            }
        }

        version = new MigrationVersion(segments);
        return true;
    }

    public int CompareTo(MigrationVersion? other) {
        if (other is null) {
            return 1;
        }

        var length = Math.Max(_segments.Length, other._segments.Length);
        for (var i = 0; i < length; i++) {
            var left = i < _segments.Length ? _segments[i] : 0;
            var right = i < other._segments.Length ? other._segments[i] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0) {
                return comparison;
            }
        }

        return 0;
    }

    public bool Equals(MigrationVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is MigrationVersion other && Equals(other);

    public override int GetHashCode() {
        // Trailing zero segments must not affect the hash: 1.0 equals 1.
        var significant = _segments.Length;
        while (significant > 0 && _segments[significant - 1] == 0) {
            significant--;
        }

        var hash = new HashCode();
        for (var i = 0; i < significant; i++) {
            hash.Add(_segments[i]);
        }

        return hash.ToHashCode();
    }

    public override string ToString() => Text;
}
