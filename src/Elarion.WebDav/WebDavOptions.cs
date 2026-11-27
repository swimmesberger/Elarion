namespace Elarion.WebDav;

/// <summary>
/// The WebDAV settings (ADR-0111), bound by <c>AddElarionWebDav(configuration)</c> from the <see cref="SectionName"/>
/// section, for example <c>WebDav:Prefix</c>. Limits are named <c>Max…</c> as in the format packages.
/// </summary>
public sealed class WebDavOptions {
    /// <summary>The conventional configuration section: <c>WebDav</c>.</summary>
    public const string SectionName = "WebDav";

    /// <summary>The default of <see cref="Prefix"/>: <c>/dav</c>.</summary>
    public const string DefaultPrefix = "/dav";

    /// <summary>The default of <see cref="MaxBodyBytes"/>: 1 MiB.</summary>
    public const long DefaultMaxBodyBytes = 1024 * 1024;

    /// <summary>The default of <see cref="MaxXmlDepth"/>: 32.</summary>
    public const int DefaultMaxXmlDepth = 32;

    /// <summary>The default of <see cref="MaxXmlElements"/>: 10,000.</summary>
    public const int DefaultMaxXmlElements = 10_000;

    /// <summary>The default of <see cref="MaxMultigetHrefs"/>: 1,000.</summary>
    public const int DefaultMaxMultigetHrefs = 1_000;

    /// <summary>The default of <see cref="MaxJournalRowsPerCollection"/>: 10,000.</summary>
    public const int DefaultMaxJournalRowsPerCollection = 10_000;

    /// <summary>The default of <see cref="JournalRetention"/>: 90 days.</summary>
    public static readonly TimeSpan DefaultJournalRetention = TimeSpan.FromDays(90);

    /// <summary>
    /// The path the DAV routes are served under, starting with <c>/</c> and without a trailing one. Default
    /// <c>/dav</c>: the principal is <c>/dav/principals/{user}/</c>, a calendar <c>/dav/calendars/{user}/{source}.{key}/</c>.
    /// </summary>
    public string Prefix { get; set; } = DefaultPrefix;

    /// <summary>
    /// The largest request body read; a larger one answers <c>413</c> without reading the rest (spec 0006 FR-025).
    /// Default 1 MiB.
    /// </summary>
    public long MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    /// <summary>The deepest element nesting of a request body; deeper answers <c>400</c>. Default 32.</summary>
    public int MaxXmlDepth { get; set; } = DefaultMaxXmlDepth;

    /// <summary>The most elements in a request body; more answer <c>413</c>. Default 10,000.</summary>
    public int MaxXmlElements { get; set; } = DefaultMaxXmlElements;

    /// <summary>The most <c>href</c>s one multiget report may name; more answer <c>413</c>. Default 1,000.</summary>
    public int MaxMultigetHrefs { get; set; } = DefaultMaxMultigetHrefs;

    /// <summary>
    /// How long a collection's change journal keeps a change (spec 0006 FR-021). A sync token older than the oldest kept
    /// change answers <c>403</c> with <c>valid-sync-token</c>, and the client resyncs in full. Pruning happens while a
    /// collection changes, so no job has to run. Default 90 days.
    /// </summary>
    public TimeSpan JournalRetention { get; set; } = DefaultJournalRetention;

    /// <summary>
    /// The most changes a collection's journal keeps, whatever their age; older ones are pruned when the collection
    /// changes. Default 10,000.
    /// </summary>
    public int MaxJournalRowsPerCollection { get; set; } = DefaultMaxJournalRowsPerCollection;

    /// <summary>
    /// Whether a user who sees no collection of any protocol is refused the whole DAV surface with <c>403</c> (every
    /// method but <c>OPTIONS</c>), instead of seeing empty homes. For an application whose DAV access was one permission
    /// before its sources decided per collection. Default false: discovery succeeds and the homes list nothing.
    /// </summary>
    public bool RefuseWhenNoCollections { get; set; }

    /// <summary>The problems of the current values; empty when they are valid.</summary>
    internal IEnumerable<string> Validate() {
        if (string.IsNullOrEmpty(Prefix) || Prefix[0] != '/' || Prefix.EndsWith('/') || Prefix.Contains("//", StringComparison.Ordinal)
            || Prefix.Any(static c => char.IsWhiteSpace(c) || c is '?' or '#' or '%'))
            yield return "WebDav:Prefix must start with '/', must not end with '/', and must be a plain path such as '/dav'.";
        if (MaxBodyBytes < 1024) yield return "WebDav:MaxBodyBytes must be at least 1024.";
        if (MaxXmlDepth < 4) yield return "WebDav:MaxXmlDepth must be at least 4.";
        if (MaxXmlElements < 16) yield return "WebDav:MaxXmlElements must be at least 16.";
        if (MaxMultigetHrefs < 1) yield return "WebDav:MaxMultigetHrefs must be at least 1.";
        if (JournalRetention <= TimeSpan.Zero) yield return "WebDav:JournalRetention must be positive.";
        if (MaxJournalRowsPerCollection < 1) yield return "WebDav:MaxJournalRowsPerCollection must be at least 1.";
    }
}
