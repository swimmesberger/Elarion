using Elarion.Abstractions.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.WebDav;

/// <summary>
/// The state of one DAV request: the request, its services (the request's DI scope, seeded with the authenticated
/// user), the current user, the options, and the collections each protocol resolved for the user, resolved at most once.
/// </summary>
public sealed class DavContext {
    private readonly Dictionary<IDavProtocol, IReadOnlyList<DavCollection>> _collections = [];
    private IDavCollectionStore? _store;

    internal DavContext(DavRequest request, IServiceProvider services, ICurrentUser user, WebDavOptions options,
        IReadOnlyList<IDavProtocol> protocols) {
        Request = request;
        Services = services;
        User = user;
        Options = options;
        Protocols = protocols;
        HrefBase = request.HrefBase.TrimEnd('/');
        PrincipalHref = HrefBase + "/principals/" + Escape(user.UserId) + "/";
    }

    /// <summary>The request.</summary>
    public DavRequest Request { get; }

    /// <summary>The request's services.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The authenticated user.</summary>
    public ICurrentUser User { get; }

    /// <summary>The WebDAV options.</summary>
    public WebDavOptions Options { get; }

    /// <summary>The registered protocols.</summary>
    public IReadOnlyList<IDavProtocol> Protocols { get; }

    /// <summary>
    /// Values a protocol keeps for the rest of the request, such as the time zone a report named, which its property
    /// writers read when they render the members.
    /// </summary>
    public IDictionary<string, object?> Items { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>The store, resolved on first use.</summary>
    public IDavCollectionStore Store => _store ??= Services.GetService<IDavCollectionStore>() ?? throw new InvalidOperationException(
        "No IDavCollectionStore is registered. Call AddElarionWebDavEntityFrameworkCore<TDbContext>() "
        + "(Elarion.WebDav.EntityFrameworkCore), AddElarionInMemoryDavStore() for tests, or register a store of your own.");

    /// <summary>The prefix path every <c>href</c> starts with, without a trailing slash, such as <c>/dav</c>.</summary>
    public string HrefBase { get; }

    /// <summary>The current user's principal URL path, such as <c>/dav/principals/u1/</c>.</summary>
    public string PrincipalHref { get; }

    /// <summary>The URL path of the principal collection, <c>/dav/principals/</c>.</summary>
    public string PrincipalsHref => HrefBase + "/principals/";

    /// <summary>The current user's home URL path of <paramref name="protocol"/>, such as <c>/dav/calendars/u1/</c>.</summary>
    public string HomeHref(IDavProtocol protocol) => HrefBase + "/" + protocol.HomeSegment + "/" + Escape(User.UserId) + "/";

    /// <summary>The URL path of <paramref name="collection"/> in the current user's home.</summary>
    public string CollectionHref(IDavProtocol protocol, DavCollection collection) => HomeHref(protocol) + Escape(collection.Segment) + "/";

    /// <summary>The URL path of member <paramref name="name"/> of <paramref name="collection"/>.</summary>
    public string MemberHref(IDavProtocol protocol, DavCollection collection, string name) => CollectionHref(protocol, collection) + Escape(name);

    /// <summary>
    /// The collections of <paramref name="protocol"/> the current user may see, resolved once per request through the
    /// protocol's access rule.
    /// </summary>
    public async ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(IDavProtocol protocol, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(protocol);
        if (_collections.TryGetValue(protocol, out var cached)) return cached;
        var collections = await protocol.GetCollectionsAsync(this, cancellationToken).ConfigureAwait(false);
        _collections[protocol] = collections;
        return collections;
    }

    /// <summary>Percent-encodes one URL path segment.</summary>
    public static string Escape(string segment) => Uri.EscapeDataString(segment);
}

/// <summary>The kinds of resource the dispatcher serves.</summary>
public enum DavResourceKind {
    /// <summary>The prefix itself, the context path of RFC 6764.</summary>
    Root,

    /// <summary>The principal collection, <c>/dav/principals/</c>.</summary>
    Principals,

    /// <summary>The current user's principal.</summary>
    Principal,

    /// <summary>The current user's home of one protocol, such as the calendar home.</summary>
    Home,

    /// <summary>A published collection, such as a calendar.</summary>
    Collection,

    /// <summary>A member of a published collection, such as a calendar object.</summary>
    Member,
}

/// <summary>A resource a request addresses or a <c>multistatus</c> lists.</summary>
public sealed class DavResource {
    /// <summary>Creates a resource.</summary>
    public DavResource(DavContext context, DavResourceKind kind, string href) {
        Context = context;
        Kind = kind;
        Href = href;
    }

    /// <summary>The request's context.</summary>
    public DavContext Context { get; }

    /// <summary>The kind of resource.</summary>
    public DavResourceKind Kind { get; }

    /// <summary>The resource's URL path, percent-encoded, with a trailing slash for collections.</summary>
    public string Href { get; }

    /// <summary>The protocol of a home, collection or member.</summary>
    public IDavProtocol? Protocol { get; init; }

    /// <summary>The collection of a collection or member resource.</summary>
    public DavCollection? Collection { get; init; }

    /// <summary>The collection's sync state, for a collection resource.</summary>
    public DavCollectionState? State { get; init; }

    /// <summary>The member, for a member resource; a <see cref="DavStoredObject"/> when its content was loaded.</summary>
    public DavMember? Member { get; init; }

    /// <summary>The member's content, when it was loaded.</summary>
    public ReadOnlyMemory<byte>? Content => Member is DavStoredObject { Content.Length: > 0 } stored ? stored.Content : null;
}
