using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Elarion.WebDav;

/// <summary>
/// The address of a published collection in the store: its kind (<c>calendar</c>), the source that declares it and its
/// key within the source. Its <see cref="Id"/> is derived from the three, so every node computes the same id, and a
/// collection nothing was published to yet still has a stable id and sync token.
/// </summary>
public sealed record DavCollectionAddress {
    private Guid? _id;

    /// <summary>Creates an address.</summary>
    /// <param name="kind">The collection kind, such as <c>calendar</c>.</param>
    /// <param name="source">The source name.</param>
    /// <param name="key">The key within the source.</param>
    public DavCollectionAddress(string kind, string source, string key) {
        ArgumentException.ThrowIfNullOrEmpty(kind);
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(key);
        Kind = kind;
        Source = source;
        Key = key;
    }

    /// <summary>The collection kind, such as <c>calendar</c>.</summary>
    public string Kind { get; }

    /// <summary>The source name.</summary>
    public string Source { get; }

    /// <summary>The key within the source.</summary>
    public string Key { get; }

    /// <summary>
    /// The collection's id: a name-based UUID (version 8) over SHA-256 of kind, source and key. It is the store's
    /// primary key and part of every sync token, so a token of another collection is recognised as foreign.
    /// </summary>
    public Guid Id => _id ??= ComputeId(Kind, Source, Key);

    /// <summary>The URL segment of the collection: <c>{source}.{key}</c>.</summary>
    public string Segment => Source + "." + Key;

    /// <inheritdoc />
    public override string ToString() => Kind + ":" + Segment;

    private static Guid ComputeId(string kind, string source, string key) {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\n" + source + "\n" + key), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8 (RFC 9562): custom, here name-based over SHA-256
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 9562 variant
        return new Guid(bytes, bigEndian: true);
    }
}

/// <summary>The sync state of a collection: its counter and the oldest counter its journal still answers from.</summary>
/// <param name="Address">The collection.</param>
/// <param name="SyncSeq">The counter, incremented by every change; 0 for a collection nothing was published to.</param>
/// <param name="LowWater">The highest counter whose change was pruned: a token below it is no longer valid.</param>
public sealed record DavCollectionState(DavCollectionAddress Address, long SyncSeq, long LowWater) {
    /// <summary>The RFC 6578 sync token of the current state, which also serves as the CTag.</summary>
    public string SyncToken => DavSyncToken.Format(Address.Id, SyncSeq);
}

/// <summary>A member of a collection, without its content.</summary>
public record DavMember {
    /// <summary>The member's name, the last segment of its URL, such as <c>shift-1.ics</c>.</summary>
    public required string Name { get; init; }

    /// <summary>The strong entity tag, quoted, such as <c>"3q2-7w"</c>.</summary>
    public required string ETag { get; init; }

    /// <summary>The media type, such as <c>text/calendar; charset=utf-8; component=vevent</c>.</summary>
    public required string ContentType { get; init; }

    /// <summary>The length of the content in bytes.</summary>
    public required long Size { get; init; }
}

/// <summary>A member of a collection with its content.</summary>
public sealed record DavStoredObject : DavMember {
    /// <summary>The identifier the object's protocol keys it by, such as the iCalendar <c>UID</c>.</summary>
    public required string Uid { get; init; }

    /// <summary>The stored bytes; empty when the store was asked for members without content.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }
}

/// <summary>
/// An object to publish: its name, identifier, bytes, entity tag and the time span its occurrences cover, which the
/// store indexes so a time-range query selects candidates without reading every object.
/// </summary>
public sealed record DavObjectContent {
    /// <summary>The member name, stable for the same <see cref="Uid"/>.</summary>
    public required string Name { get; init; }

    /// <summary>The identifier the protocol keys the object by.</summary>
    public required string Uid { get; init; }

    /// <summary>The bytes to store.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The media type.</summary>
    public required string ContentType { get; init; }

    /// <summary>The strong entity tag, quoted; <see cref="DavETag.Compute"/> derives it from the bytes.</summary>
    public required string ETag { get; init; }

    /// <summary>
    /// The first instant the object covers, or <see langword="null"/> when it has no time, which makes it a candidate of
    /// every time-range query.
    /// </summary>
    public DateTimeOffset? RangeStart { get; init; }

    /// <summary>The last instant the object covers, or <see langword="null"/> when it is unbounded (an endless series).</summary>
    public DateTimeOffset? RangeEnd { get; init; }
}

/// <summary>What a change did to a member, as the journal records it.</summary>
public enum DavChangeKind {
    /// <summary>The member was added.</summary>
    Added = 1,

    /// <summary>The member's content changed.</summary>
    Changed = 2,

    /// <summary>The member was removed.</summary>
    Removed = 3,
}

/// <summary>One journal row: a member's change and its counter.</summary>
/// <param name="Name">The member name.</param>
/// <param name="Kind">What changed.</param>
/// <param name="Seq">The collection counter the change was given.</param>
public readonly record struct DavChange(string Name, DavChangeKind Kind, long Seq);

/// <summary>The changes of a collection after a token, up to its current counter, oldest first.</summary>
/// <param name="CurrentSeq">The collection counter the changes end at.</param>
/// <param name="Changes">The journal rows after the token, ordered by counter.</param>
public sealed record DavChangeSet(long CurrentSeq, IReadOnlyList<DavChange> Changes);

/// <summary>What a publish did.</summary>
public enum DavPublishOutcome {
    /// <summary>The stored bytes were equal: nothing was written and the sync token did not change.</summary>
    Unchanged,

    /// <summary>The object was added.</summary>
    Added,

    /// <summary>The object's content changed.</summary>
    Changed,
}

/// <summary>What a replace did: how many objects it added, changed and removed.</summary>
/// <param name="Added">The objects added.</param>
/// <param name="Changed">The objects whose content changed.</param>
/// <param name="Removed">The objects removed because the new set lacked them.</param>
public readonly record struct DavReplaceOutcome(int Added, int Changed, int Removed) {
    /// <summary>Whether the replace changed anything, and so the sync token.</summary>
    public bool HasChanges => Added + Changed + Removed > 0;
}

/// <summary>Strong entity tags derived from content.</summary>
public static class DavETag {
    /// <summary>The quoted, strong entity tag of <paramref name="content"/>: base64url of the first 16 bytes of its SHA-256.</summary>
    public static string Compute(ReadOnlySpan<byte> content) {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(content, hash);
        return "\"" + Convert.ToBase64String(hash[..16]).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "\"";
    }

    /// <summary>Whether an <c>If-None-Match</c> or <c>If-Match</c> header value names <paramref name="etag"/> or <c>*</c>.</summary>
    public static bool Matches(string? header, string etag) {
        if (string.IsNullOrWhiteSpace(header)) return false;
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)) {
            if (part == "*") return true;
            var candidate = part.StartsWith("W/", StringComparison.Ordinal) ? part[2..] : part;
            if (string.Equals(candidate, etag, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}

/// <summary>
/// The RFC 6578 sync token: <c>urn:elarion:sync:{collection id}:{counter}</c>. A URI, so Evolution does not take it for
/// a CTag; it holds the collection's id, so a token of another collection is refused; and it holds only the counter,
/// so it is valid on every node and after a restart (spec 0006 FR-020).
/// </summary>
public static class DavSyncToken {
    /// <summary>The token's prefix.</summary>
    public const string Prefix = "urn:elarion:sync:";

    /// <summary>The token for <paramref name="collectionId"/> at <paramref name="seq"/>.</summary>
    public static string Format(Guid collectionId, long seq) =>
        Prefix + collectionId.ToString("D") + ":" + seq.ToString(CultureInfo.InvariantCulture);

    /// <summary>Reads a token; false when it is not one of this format.</summary>
    public static bool TryParse(string? token, out Guid collectionId, out long seq) {
        collectionId = Guid.Empty;
        seq = 0;
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = token.AsSpan(Prefix.Length);
        var colon = rest.IndexOf(':');
        return colon > 0
               && Guid.TryParseExact(rest[..colon], "D", out collectionId)
               && long.TryParse(rest[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out seq);
    }
}
