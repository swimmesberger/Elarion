using System.Text;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>Who may see what in the test sources; the handlers read it as the application's permissions would be read.</summary>
internal sealed class CardDavTestDirectory {
    public HashSet<string> CustomerReaders { get; } = new(StringComparer.Ordinal) { "alice", "bob" };

    public Dictionary<string, HashSet<string>> Teams { get; } = new(StringComparer.Ordinal) {
        ["red"] = new(StringComparer.Ordinal) { "alice", "bob" },
    };

    public bool GateOpen { get; set; } = true;

    public List<PublishedAddressBookInfo>? Override { get; set; }
}

/// <summary>The "Customers" source: one address book for everyone who may read customers.</summary>
public sealed record CustomerAddressBooks : ICardDavSource<CustomerAddressBooks> {
    public static string SourceName => "customers";
}

/// <summary>The team source: one address book per team the user belongs to (a resource rule).</summary>
public sealed record TeamAddressBooks : ICardDavSource<TeamAddressBooks> {
    public static string SourceName => "teams";
}

/// <summary>A source behind a feature gate: a closed gate answers not-found.</summary>
public sealed record GatedAddressBooks : ICardDavSource<GatedAddressBooks> {
    public static string SourceName => "gated";
}

internal sealed class ListCustomerAddressBooks(ICurrentUser user, CardDavTestDirectory directory)
    : IHandler<CustomerAddressBooks, Result<CardDavSourceListing>> {
    public ValueTask<Result<CardDavSourceListing>> HandleAsync(CustomerAddressBooks request, CancellationToken ct) {
        if (directory.Override is { } listing) return ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing(listing)));
        if (!directory.CustomerReaders.Contains(user.UserId))
            return ValueTask.FromResult(Result<CardDavSourceListing>.Failure(AppError.Forbidden("Missing permission customers.read.")));
        return ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([
            new PublishedAddressBookInfo("all", "Customers") { Description = "Every active customer" },
        ])));
    }
}

internal sealed class ListTeamAddressBooks(ICurrentUser user, CardDavTestDirectory directory) : IHandler<TeamAddressBooks, Result<CardDavSourceListing>> {
    public ValueTask<Result<CardDavSourceListing>> HandleAsync(TeamAddressBooks request, CancellationToken ct) =>
        ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([
            .. directory.Teams.Where(team => team.Value.Contains(user.UserId)).OrderBy(static t => t.Key, StringComparer.Ordinal)
                .Select(static team => new PublishedAddressBookInfo(team.Key, "Team " + team.Key)),
        ])));
}

internal sealed class ListGatedAddressBooks(CardDavTestDirectory directory) : IHandler<GatedAddressBooks, Result<CardDavSourceListing>> {
    public ValueTask<Result<CardDavSourceListing>> HandleAsync(GatedAddressBooks request, CancellationToken ct) =>
        ValueTask.FromResult(directory.GateOpen
            ? Result<CardDavSourceListing>.Success(new CardDavSourceListing([new PublishedAddressBookInfo("all", "Gated")]))
            : Result<CardDavSourceListing>.Failure(AppError.NotFound("Not found.")));
}

internal static class CardDavTestData {
    // The smallest valid PNG: a 1×1 transparent pixel.
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

    public static Contact Person(string uid, string given, string family, string? email = null, string? phone = null, string? note = null) => new() {
        Uid = uid,
        FormattedName = given + " " + family,
        Name = new ContactName(family, given, null, null, null),
        Emails = email is null ? [] : [new ContactEmail(email) { Types = ContactTypes.Work }],
        Phones = phone is null ? [] : [new ContactPhone(phone) { Types = ContactTypes.Cell }],
        Note = note,
    };
}

/// <summary>A container with CardDAV, the in-memory store (or another), the test sources and a scoped test user.</summary>
internal sealed class CardDavTestHarness : IAsyncDisposable {
    private CardDavTestHarness(ServiceProvider services) {
        Services = services;
    }

    public ServiceProvider Services { get; }

    public CardDavTestDirectory Directory => Services.GetRequiredService<CardDavTestDirectory>();

    public static CardDavTestHarness Create(Action<IServiceCollection>? configure = null, bool registerGated = true, Action<WebDavOptions>? webDav = null) {
        var services = new ServiceCollection();
        services.AddElarionCardDav();
        services.AddElarionWebDav(configure: webDav);
        services.AddElarionInMemoryDavStore();
        services.AddSingleton<CardDavTestDirectory>();
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUserAccessor>().User);
        services.AddScoped<IHandler<CustomerAddressBooks, Result<CardDavSourceListing>>, ListCustomerAddressBooks>();
        services.AddScoped<IHandler<TeamAddressBooks, Result<CardDavSourceListing>>, ListTeamAddressBooks>();
        services.AddElarionCardDavSource<CustomerAddressBooks>();
        services.AddElarionCardDavSource<TeamAddressBooks>();
        if (registerGated) {
            services.AddScoped<IHandler<GatedAddressBooks, Result<CardDavSourceListing>>, ListGatedAddressBooks>();
            services.AddElarionCardDavSource<GatedAddressBooks>();
        }

        configure?.Invoke(services);
        return new CardDavTestHarness(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }));
    }

    public async Task<T> PublisherAsync<T>(Func<ICardDavPublisher, Task<T>> action) {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<ICardDavPublisher>());
    }

    public Task<bool> PublishAsync(string source, string key, Contact contact) =>
        PublisherAsync(p => p.PublishAsync(new CardDavAddress(source, key), contact, TestContext.Current.CancellationToken).AsTask());

    public async Task<DavTestResponse> SendAsync(string method, string path, string? body = null, string user = "alice",
        IDictionary<string, string>? headers = null) {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = new TestDavUser(user, user + "@example.com");
        var response = await scope.ServiceProvider.GetRequiredService<DavDispatcher>().HandleAsync(new DavRequest {
            Method = method,
            Path = path,
            HrefBase = "/dav",
            Headers = new Dictionary<string, string>(headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
            Body = body is null ? Stream.Null : new MemoryStream(Encoding.UTF8.GetBytes(body)),
        }, scope.ServiceProvider, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();
        await response.WriteBodyAsync(output, TestContext.Current.CancellationToken);
        return new DavTestResponse(response.StatusCode, response.Headers, response.ContentType, output.ToArray());
    }

    public const string Namespaces = "xmlns:D=\"DAV:\" xmlns:CARD=\"urn:ietf:params:xml:ns:carddav\" xmlns:CS=\"http://calendarserver.org/ns/\"";

    public static string PropFind(params string[] props) =>
        $"<D:propfind {Namespaces}><D:prop>{string.Concat(props.Select(static p => "<" + p + "/>"))}</D:prop></D:propfind>";

    public static string SyncCollection(string? token, string addressData = "") =>
        $"<D:sync-collection {Namespaces}><D:sync-token>{token}</D:sync-token><D:sync-level>1</D:sync-level><D:prop><D:getetag/>{addressData}</D:prop></D:sync-collection>";

    public static string Multiget(string addressData, params string[] hrefs) =>
        $"<CARD:addressbook-multiget {Namespaces}><D:prop><D:getetag/>{addressData}</D:prop>"
        + string.Concat(hrefs.Select(static h => "<D:href>" + h + "</D:href>")) + "</CARD:addressbook-multiget>";

    public static string Query(string? filter, string addressData = "<CARD:address-data/>", int? limit = null, string test = "") =>
        $"<CARD:addressbook-query {Namespaces}><D:prop><D:getetag/>{addressData}</D:prop>"
        + (filter is null ? "" : $"<CARD:filter{test}>{filter}</CARD:filter>")
        + (limit is null ? "" : $"<CARD:limit><CARD:nresults>{limit}</CARD:nresults></CARD:limit>")
        + "</CARD:addressbook-query>";

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
