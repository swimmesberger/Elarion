namespace Elarion.WebDav.EntityFrameworkCore;

/// <summary>A published collection: its address, its change counter and its journal's low-water mark.</summary>
public sealed class DavCollectionEntity {
    /// <summary>The collection's id, <see cref="DavCollectionAddress.Id"/> (the primary key).</summary>
    public Guid Id { get; set; }

    /// <summary>The collection kind, such as <c>calendar</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The source name.</summary>
    public required string Source { get; set; }

    /// <summary>The key within the source.</summary>
    public required string Key { get; set; }

    /// <summary>The change counter: the sync token's number.</summary>
    public long SyncSeq { get; set; }

    /// <summary>The highest counter whose journal row was pruned; a token below it is invalid.</summary>
    public long LowWater { get; set; }
}

/// <summary>A published object: its bytes, entity tag and the time span it covers.</summary>
public sealed class DavObjectEntity {
    /// <summary>The collection's id.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>The member name, unique in the collection.</summary>
    public required string Name { get; set; }

    /// <summary>The object's identifier (the iCalendar <c>UID</c>), unique in the collection.</summary>
    public required string Uid { get; set; }

    /// <summary>The strong entity tag, quoted.</summary>
    public required string ETag { get; set; }

    /// <summary>The stored bytes.</summary>
    public required byte[] Content { get; set; }

    /// <summary>The media type.</summary>
    public required string ContentType { get; set; }

    /// <summary>The first instant covered, or <see langword="null"/> for an object without a time.</summary>
    public DateTimeOffset? RangeStart { get; set; }

    /// <summary>The last instant covered, or <see langword="null"/> when unbounded.</summary>
    public DateTimeOffset? RangeEnd { get; set; }

    /// <summary>The length of <see cref="Content"/>.</summary>
    public long Size { get; set; }
}

/// <summary>A row of a collection's change journal.</summary>
public sealed class DavChangeEntity {
    /// <summary>The collection's id.</summary>
    public Guid CollectionId { get; set; }

    /// <summary>The counter the change was given.</summary>
    public long Seq { get; set; }

    /// <summary>The member name.</summary>
    public required string Name { get; set; }

    /// <summary>What changed: 1 added, 2 changed, 3 removed (<see cref="DavChangeKind"/>).</summary>
    public short Op { get; set; }

    /// <summary>When the change was recorded, for the journal's retention.</summary>
    public DateTimeOffset ChangedAt { get; set; }
}
