using System.Text;
using System.Xml;
using Elarion.Abstractions.Identity;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>A signed-in user for dispatcher tests.</summary>
internal sealed class TestDavUser(string userId, string? email = null) : ICurrentUser {
    public string UserId { get; } = userId;

    public string? Email { get; } = email;

    public IReadOnlyList<string> Roles { get; } = [];

    public bool IsAuthenticated => UserId.Length > 0;

    public bool IsInRole(string role) => false;

    public IEnumerable<string> GetClaimValues(string type) => [];
}

/// <summary>Holds the user a dispatcher call runs as; the scoped <see cref="ICurrentUser"/> reads it.</summary>
internal sealed class TestUserAccessor {
    public ICurrentUser User { get; set; } = new TestDavUser("");
}

/// <summary>
/// A test-only collection kind (slice 2 of plan 0006): <c>/dav/things/{user}/{source}.{key}/</c>, with one custom
/// property per resource kind and a member property that needs the content.
/// </summary>
internal sealed class TestThingsProtocol : IDavProtocol {
    public const string Ns = "urn:elarion:test";
    public const string Kind = "thing";

    /// <summary>Which collections each user sees.</summary>
    public Dictionary<string, List<DavCollection>> Visible { get; } = new(StringComparer.Ordinal);

    public int ResolveCount { get; private set; }

    public string CollectionKind => Kind;

    public string HomeSegment => "things";

    public string WellKnownName => "things";

    public IReadOnlyList<string> ComplianceClasses => ["thing-access"];

    public IReadOnlyList<DavName> CollectionResourceTypes => [new(Ns, "things")];

    public IReadOnlyList<DavName> CollectionReports => [];

    public IReadOnlyList<DavProperty> PrincipalProperties => [
        new(new DavName(Ns, "thing-home-set"), false, null, (w, r, _, _) => {
            w.WriteElementString("href", DavNames.Dav, r.Context.HomeHref(this));
            return ValueTask.CompletedTask;
        }),
    ];

    public IReadOnlyList<DavProperty> CollectionProperties => [
        new(new DavName(Ns, "shape"), true, null, (w, r, _, _) => {
            w.WriteString("round:" + r.Collection!.Segment);
            return ValueTask.CompletedTask;
        }),
    ];

    public IReadOnlyList<DavProperty> MemberProperties => [
        new(new DavName(Ns, "data"), false, r => r.Content is not null, (w, r, _, _) => {
            w.WriteString(Encoding.UTF8.GetString(r.Content!.Value.Span));
            return ValueTask.CompletedTask;
        }),
    ];

    public void Show(string user, string source, string key, string? name = null) {
        if (!Visible.TryGetValue(user, out var list)) Visible[user] = list = [];
        list.Add(new DavCollection(Address(source, key), name ?? source + " " + key));
    }

    public static DavCollectionAddress Address(string source, string key) => new(Kind, source, key);

    public ValueTask<IReadOnlyList<DavCollection>> GetCollectionsAsync(DavContext context, CancellationToken cancellationToken) {
        ResolveCount++;
        return ValueTask.FromResult<IReadOnlyList<DavCollection>>(Visible.TryGetValue(context.User.UserId, out var list) ? list : []);
    }

    public ValueTask<DavResponse?> ReportAsync(DavContext context, DavResource collection, DavXmlElement report, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DavResponse?>(null);

    public ValueTask<DavResponse?> HandleOtherAsync(DavContext context, IReadOnlyList<string> segments, CancellationToken cancellationToken) =>
        ValueTask.FromResult<DavResponse?>(null);
}

/// <summary>What a dispatcher call answered.</summary>
internal sealed record DavTestResponse(int Status, IDictionary<string, string> Headers, string? ContentType, byte[] Bytes) {
    public string Body => Encoding.UTF8.GetString(Bytes);

    /// <summary>The body as an element tree (no limits needed for the server's own output).</summary>
    public DavXmlElement Xml => DavXml.Parse(Bytes, new DavXmlLimits(long.MaxValue, 1000, int.MaxValue)).Root
                                ?? throw new InvalidOperationException("The body is not XML: " + Body);

    /// <summary>The <c>response</c> elements of a multistatus, by href.</summary>
    public IReadOnlyDictionary<string, DavXmlElement> Responses =>
        Xml.ChildrenNamed(DavNames.D.Response).ToDictionary(r => r.Child(DavNames.D.Href)!.Text.Trim(), StringComparer.Ordinal);

    /// <summary>The value element of property <paramref name="name"/> in the 200 propstat of <paramref name="href"/>, or null.</summary>
    public DavXmlElement? Prop(string href, DavName name) => PropWithStatus(href, name, "200");

    /// <summary>Whether <paramref name="name"/> is in the 404 propstat of <paramref name="href"/>.</summary>
    public bool IsMissing(string href, DavName name) => PropWithStatus(href, name, "404") is not null;

    public string? SyncToken => Xml.Child(DavNames.D.SyncToken)?.Text.Trim();

    private DavXmlElement? PropWithStatus(string href, DavName name, string status) {
        if (!Responses.TryGetValue(href, out var response)) return null;
        foreach (var propstat in response.ChildrenNamed(DavNames.D.Propstat)) {
            if (!(propstat.Child(DavNames.D.Status)?.Text ?? "").Contains(" " + status + " ", StringComparison.Ordinal)) continue;
            if (propstat.Child(DavNames.D.Prop)?.Child(name) is { } found) return found;
        }

        return null;
    }
}

/// <summary>A DI container with the WebDAV core, a store and the test protocol, and a way to send it requests.</summary>
internal sealed class DavTestHarness : IAsyncDisposable {
    private DavTestHarness(ServiceProvider services, TestThingsProtocol protocol) {
        Services = services;
        Protocol = protocol;
    }

    public ServiceProvider Services { get; }

    public TestThingsProtocol Protocol { get; }

    public static DavTestHarness Create(Action<IServiceCollection>? configure = null, Action<WebDavOptions>? options = null) {
        var protocol = new TestThingsProtocol();
        var services = new ServiceCollection();
        services.AddElarionWebDav(configure: options);
        services.AddElarionInMemoryDavStore();
        services.AddSingleton<IDavProtocol>(protocol);
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUserAccessor>().User);
        configure?.Invoke(services);
        return new DavTestHarness(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }), protocol);
    }

    public IDavCollectionStore CreateStore(out AsyncServiceScope scope) {
        scope = Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<IDavCollectionStore>();
    }

    public async Task<DavPublishOutcome> PublishAsync(string source, string key, string uid, string text) {
        await using var scope = Services.CreateAsyncScope();
        var bytes = Encoding.UTF8.GetBytes(text);
        return await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>().PublishAsync(TestThingsProtocol.Address(source, key),
            new DavObjectContent {
                Name = uid + ".txt", Uid = uid, Content = bytes, ContentType = "text/plain; charset=utf-8", ETag = DavETag.Compute(bytes),
            }, TestContext.Current.CancellationToken);
    }

    public async Task<bool> RemoveAsync(string source, string key, string uid) {
        await using var scope = Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDavCollectionStore>()
            .RemoveAsync(TestThingsProtocol.Address(source, key), uid + ".txt", TestContext.Current.CancellationToken);
    }

    public async Task<DavTestResponse> SendAsync(string method, string path, string? body = null, string user = "u1",
        IDictionary<string, string>? headers = null, Stream? bodyStream = null, long? contentLength = null) {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = new TestDavUser(user, user + "@example.com");
        var dispatcher = scope.ServiceProvider.GetRequiredService<DavDispatcher>();
        var request = new DavRequest {
            Method = method,
            Path = path,
            HrefBase = "/dav",
            Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Body = bodyStream ?? (body is null ? Stream.Null : new MemoryStream(Encoding.UTF8.GetBytes(body))),
            ContentLength = contentLength ?? (body is null ? null : Encoding.UTF8.GetByteCount(body)),
        };
        var response = await dispatcher.HandleAsync(request, scope.ServiceProvider, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await response.WriteBodyAsync(output, TestContext.Current.CancellationToken);
        return new DavTestResponse(response.StatusCode, response.Headers, response.ContentType, output.ToArray());
    }

    public static string PropFind(params string[] props) =>
        "<?xml version=\"1.0\"?><D:propfind xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\" xmlns:CS=\"http://calendarserver.org/ns/\" "
        + "xmlns:ICAL=\"http://apple.com/ns/ical/\" xmlns:T=\"urn:elarion:test\"><D:prop>"
        + string.Concat(props.Select(static p => "<" + p + "/>")) + "</D:prop></D:propfind>";

    public static string SyncCollection(string? token, int? limit = null, string level = "1", params string[] props) =>
        "<?xml version=\"1.0\"?><D:sync-collection xmlns:D=\"DAV:\" xmlns:T=\"urn:elarion:test\"><D:sync-token>" + XmlEscape(token ?? "")
        + "</D:sync-token><D:sync-level>" + level + "</D:sync-level>"
        + (limit is null ? "" : "<D:limit><D:nresults>" + limit + "</D:nresults></D:limit>")
        + "<D:prop><D:getetag/>" + string.Concat(props.Select(static p => "<" + p + "/>")) + "</D:prop></D:sync-collection>";

    private static string XmlEscape(string text) => new XmlDocument().CreateTextNode(text).OuterXml;

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
