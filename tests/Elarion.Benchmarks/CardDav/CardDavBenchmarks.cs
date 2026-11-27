using System.Text;
using BenchmarkDotNet.Attributes;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;

namespace Elarion.Benchmarks.CardDav;

// CardDAV (ADR-0113, spec 0007) through the dispatcher over the in-memory store, so the numbers are the protocol's own:
// a sync-collection from the current token of an unchanged address book of 1,000 contacts (SC-003: under 20 ms), and an
// addressbook-query over the same 1,000, which parses every card because nothing is indexed (ADR-0113 section 3).
//
//   dotnet run --project tests/Elarion.Benchmarks -c Release -- --filter "*CardDav*"
[MemoryDiagnoser]
public class CardDavBenchmarks {
    private const string Book = "/addressbooks/bench/people.all/";
    private const string Ns = "xmlns:D=\"DAV:\" xmlns:CARD=\"urn:ietf:params:xml:ns:carddav\"";

    private ServiceProvider _services = null!;
    private byte[] _sync = [];
    private byte[] _query = [];

    [GlobalSetup]
    public async Task Setup() {
        var services = new ServiceCollection();
        services.AddElarionCardDav();
        services.AddElarionInMemoryDavStore();
        services.AddSingleton<ICurrentUser>(new BenchmarkUser());
        services.AddScoped<IHandler<People, Result<CardDavSourceListing>>, ListPeople>();
        services.AddElarionCardDavSource<People>();
        _services = services.BuildServiceProvider();

        await using (var scope = _services.CreateAsyncScope()) {
            await scope.ServiceProvider.GetRequiredService<ICardDavPublisher>().ReplaceAsync(new CardDavAddress("people", "all"),
                Enumerable.Range(0, 1_000).Select(static i => new Contact {
                    Uid = "p" + i,
                    FormattedName = "Person " + i,
                    Name = new ContactName("Family" + i, "Given", null, null, null),
                    Emails = [new ContactEmail($"p{i}@example.com") { Types = ContactTypes.Work }],
                    Phones = [new ContactPhone("+43 1 " + i)],
                }));
        }

        var initial = await SendAsync(Encoding.UTF8.GetBytes(
            $"<D:sync-collection {Ns}><D:sync-token/><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>"));
        var body = Encoding.UTF8.GetString(initial);
        var start = body.IndexOf("urn:elarion:sync:", StringComparison.Ordinal);
        var token = body[start..body.IndexOf('<', start)];
        _sync = Encoding.UTF8.GetBytes(
            $"<D:sync-collection {Ns}><D:sync-token>{token}</D:sync-token><D:sync-level>1</D:sync-level><D:prop><D:getetag/></D:prop></D:sync-collection>");
        _query = Encoding.UTF8.GetBytes(
            $"<CARD:addressbook-query {Ns}><D:prop><D:getetag/></D:prop><CARD:filter><CARD:prop-filter name=\"EMAIL\">"
            + "<CARD:text-match match-type=\"starts-with\">p99</CARD:text-match></CARD:prop-filter></CARD:filter></CARD:addressbook-query>");
    }

    [GlobalCleanup]
    public ValueTask Cleanup() => _services.DisposeAsync();

    [Benchmark]
    public async Task<int> UnchangedSyncOf1000Contacts() => (await SendAsync(_sync)).Length;

    [Benchmark]
    public async Task<int> QueryOver1000Contacts() => (await SendAsync(_query)).Length;

    private async Task<byte[]> SendAsync(byte[] body) {
        await using var scope = _services.CreateAsyncScope();
        var response = await scope.ServiceProvider.GetRequiredService<DavDispatcher>().HandleAsync(new DavRequest {
            Method = "REPORT",
            Path = Book,
            HrefBase = "/dav",
            Body = new MemoryStream(body, false),
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Depth"] = "1" },
        }, scope.ServiceProvider);
        using var output = new MemoryStream();
        await response.WriteBodyAsync(output);
        return output.ToArray();
    }

    public sealed record People : ICardDavSource<People> {
        public static string SourceName => "people";
    }

    private sealed class ListPeople : IHandler<People, Result<CardDavSourceListing>> {
        public ValueTask<Result<CardDavSourceListing>> HandleAsync(People request, CancellationToken ct) =>
            ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([new PublishedAddressBookInfo("all", "People")])));
    }

    private sealed class BenchmarkUser : ICurrentUser {
        public string UserId => "bench";
        public string? Email => "bench@example.com";
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => true;
        public bool IsInRole(string role) => false;
        public IEnumerable<string> GetClaimValues(string type) => [];
    }
}
