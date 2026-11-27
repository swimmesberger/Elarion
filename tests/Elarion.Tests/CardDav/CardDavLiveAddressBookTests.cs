using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>The contacts of the live test source and who may see each, as the application's row rules would decide.</summary>
internal sealed class LiveContactDirectory {
    public Dictionary<string, (Contact Contact, Contact WithoutPhoto, int Version, HashSet<string> Viewers)> Contacts { get; } = new(StringComparer.Ordinal);

    public List<DavContentPurpose> Purposes { get; } = [];

    public void Add(Contact contact, Contact withoutPhoto, params string[] viewers) =>
        Contacts[contact.Uid!] = (contact, withoutPhoto, 1, new HashSet<string>(viewers, StringComparer.Ordinal));

    public void Add(Contact contact, params string[] viewers) => Add(contact, contact, viewers);
}

/// <summary>A live source: one address book at the segment <c>default</c>, its contacts filtered per viewer.</summary>
public sealed record PeopleAddressBooks : ICardDavSource<PeopleAddressBooks> {
    public static string SourceName => "people";
}

internal sealed class ListPeopleAddressBooks : IHandler<PeopleAddressBooks, Result<CardDavSourceListing>> {
    public ValueTask<Result<CardDavSourceListing>> HandleAsync(PeopleAddressBooks request, CancellationToken ct) =>
        ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([
            new PublishedAddressBookInfo("all", "Contacts") { Segment = "default" },
        ])));
}

/// <summary>The application's live contacts: the viewer's rows, the photo left out of searches.</summary>
internal sealed class VisiblePeople(ICurrentUser user, LiveContactDirectory directory) : ICardDavLiveAddressBooks {
    public async IAsyncEnumerable<CardDavLiveMember> ListAsync(string key, [EnumeratorCancellation] CancellationToken cancellationToken) {
        foreach (var (id, row) in Visible()) yield return new CardDavLiveMember(id + ".vcf", "v" + row.Version);
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<CardDavLiveContact> GetAsync(string key, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
        [EnumeratorCancellation] CancellationToken cancellationToken) {
        directory.Purposes.Add(purpose);
        foreach (var (id, row) in Visible()) {
            var name = id + ".vcf";
            if (names is not null && !names.Contains(name)) continue;
            var contact = purpose == DavContentPurpose.Query ? row.WithoutPhoto : row.Contact;
            yield return new CardDavLiveContact(name, "v" + row.Version, contact);
        }

        await Task.CompletedTask;
    }

    private IEnumerable<KeyValuePair<string, (Contact Contact, Contact WithoutPhoto, int Version, HashSet<string> Viewers)>> Visible() =>
        directory.Contacts.Where(c => c.Value.Viewers.Contains(user.UserId)).OrderBy(static c => c.Key, StringComparer.Ordinal);
}

/// <summary>Live address books (ADR-0113, amendment 2026-11-27; spec 0007 FR-035–FR-041).</summary>
public sealed class CardDavLiveAddressBookTests {
    private const string Book = "/addressbooks/alice/default/";
    private const string Href = "/dav" + Book;
    private static readonly DavName GetCTag = new(DavNames.CalendarServer, "getctag");

    private static CardDavTestHarness Create() {
        var harness = CardDavTestHarness.Create(services => {
            services.AddSingleton<LiveContactDirectory>();
            services.AddScoped<IHandler<PeopleAddressBooks, Result<CardDavSourceListing>>, ListPeopleAddressBooks>();
            services.AddElarionCardDavLiveSource<PeopleAddressBooks, VisiblePeople>();
        });
        var directory = harness.Services.GetRequiredService<LiveContactDirectory>();
        directory.Add(new Contact {
            Uid = "p-1", FormattedName = "Ada Lovelace", Emails = [new ContactEmail("ada@example.com")], Photo = ContactPhoto.FromData(CardDavTestData.Png),
        }, CardDavTestData.Person("p-1", "Ada", "Lovelace", "ada@example.com"), "alice", "bob");
        directory.Add(CardDavTestData.Person("p-2", "Grace", "Hopper", "grace@example.com"), "alice");
        directory.Add(CardDavTestData.Person("p-3", "Alan", "Turing", "alan@example.com"), "bob");
        return harness;
    }

    private static LiveContactDirectory Directory(CardDavTestHarness harness) => harness.Services.GetRequiredService<LiveContactDirectory>();

    [Fact]
    public async Task TheHome_ListsTheLiveAddressBookAtItsSegment_WithACTag_AndWithoutSyncTokenOrSyncCollection() {
        await using var harness = Create();

        var response = await harness.SendAsync("PROPFIND", "/addressbooks/alice/",
            CardDavTestHarness.PropFind("D:displayname", "CS:getctag", "D:sync-token", "D:supported-report-set"), headers: Depth("1"));

        response.Status.Should().Be(207);
        response.Prop(Href, DavNames.D.DisplayName)!.Text.Should().Be("Contacts");
        response.Prop(Href, GetCTag)!.Text.Should().NotBeNullOrEmpty();
        response.IsMissing(Href, DavNames.D.SyncToken).Should().BeTrue();
        var reports = response.Prop(Href, DavNames.D.SupportedReportSet)!.Body();
        reports.Should().NotContain("sync-collection").And.Contain("addressbook-multiget").And.Contain("addressbook-query");
    }

    [Fact]
    public async Task TheListing_ShowsEachViewerTheirOwnContacts_WithTheApplicationsETags() {
        await using var harness = Create();

        var alice = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:getetag", "D:getcontentlength"), headers: Depth("1"));
        var bob = await harness.SendAsync("PROPFIND", "/addressbooks/bob/default/", CardDavTestHarness.PropFind("D:getetag"), user: "bob",
            headers: Depth("1"));

        alice.Responses.Keys.Should().BeEquivalentTo(Href, Href + "p-1.vcf", Href + "p-2.vcf");
        alice.Prop(Href + "p-1.vcf", DavNames.D.GetETag)!.Text.Should().Be("\"v1\"");
        alice.IsMissing(Href + "p-1.vcf", DavNames.D.GetContentLength).Should().BeTrue();
        bob.Responses.Keys.Should().BeEquivalentTo("/dav/addressbooks/bob/default/", "/dav/addressbooks/bob/default/p-1.vcf",
            "/dav/addressbooks/bob/default/p-3.vcf");
        Directory(harness).Purposes.Should().BeEmpty("a listing writes no card");
    }

    [Fact]
    public async Task APropFindAtDepth1NamingAddressData_CarriesEachVisibleCard_WithItsETag() {
        await using var harness = Create();

        var response = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:getetag", "CARD:address-data"), headers: Depth("1"));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo(Href, Href + "p-1.vcf", Href + "p-2.vcf");
        response.Prop(Href + "p-1.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("UID:p-1").And.Contain("PHOTO", "the card is the one a GET answers");
        response.Prop(Href + "p-1.vcf", DavNames.D.GetETag)!.Text.Should().Be("\"v1\"");
        response.Prop(Href + "p-2.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("FN:Grace Hopper");
        response.IsMissing(Href, DavNames.Card.AddressData).Should().BeTrue("the address book itself has no card");
        Directory(harness).Purposes.Should().Equal(DavContentPurpose.Multiget);
    }

    [Fact]
    public async Task APropFindAtDepth0OfAContactNamingAddressData_CarriesItsCard() {
        await using var harness = Create();

        var response = await harness.SendAsync("PROPFIND", Book + "p-2.vcf", CardDavTestHarness.PropFind("D:getetag", "CARD:address-data"),
            headers: Depth("0"));

        response.Status.Should().Be(207);
        response.Prop(Href + "p-2.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("UID:p-2");
        response.Prop(Href + "p-2.vcf", DavNames.D.GetETag)!.Text.Should().Be("\"v1\"");
    }

    [Fact]
    public async Task APropFindWithoutDepth_ListsTheLiveAddressBookAsDepth1() {
        await using var harness = Create();

        var response = await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:getetag"));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().BeEquivalentTo(Href, Href + "p-1.vcf", Href + "p-2.vcf");
    }

    [Fact]
    public async Task Multiget_WritesTheVisibleCards_And404sAnInvisibleOne() {
        await using var harness = Create();

        var response = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Multiget("<CARD:address-data/>", Href + "p-1.vcf", Href + "p-3.vcf", Href + "p-2.vcf"));

        response.Status.Should().Be(207);
        var card = response.Prop(Href + "p-1.vcf", DavNames.Card.AddressData)!.Text;
        card.Should().Contain("UID:p-1").And.Contain("PHOTO");
        response.Prop(Href + "p-1.vcf", DavNames.D.GetETag)!.Text.Should().Be("\"v1\"");
        response.Responses[Href + "p-3.vcf"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
        response.Prop(Href + "p-2.vcf", DavNames.Card.AddressData)!.Text.Should().Contain("FN:Grace Hopper");
        Directory(harness).Purposes.Should().Equal(DavContentPurpose.Multiget);
    }

    [Fact]
    public async Task Query_AsksForQueryContent_SoTheSourceCanLeaveThePhotoOut() {
        await using var harness = Create();

        var response = await harness.SendAsync("REPORT", Book,
            CardDavTestHarness.Query("<CARD:prop-filter name=\"EMAIL\"><CARD:text-match match-type=\"contains\">ada</CARD:text-match></CARD:prop-filter>"));

        response.Status.Should().Be(207);
        response.Responses.Keys.Should().Equal(Href + "p-1.vcf");
        response.Prop(Href + "p-1.vcf", DavNames.Card.AddressData)!.Text.Should().NotContain("PHOTO");
        Directory(harness).Purposes.Should().Equal(DavContentPurpose.Query);
    }

    [Fact]
    public async Task Get_WritesTheCardAsVCard30_WithTheApplicationsETag_AndAnInvisibleContactIs404() {
        await using var harness = Create();

        var get = await harness.SendAsync("GET", Book + "p-2.vcf");
        var notModified = await harness.SendAsync("GET", Book + "p-2.vcf", headers: new Dictionary<string, string> { ["If-None-Match"] = "\"v1\"" });
        var invisible = await harness.SendAsync("GET", Book + "p-3.vcf");

        get.Status.Should().Be(200);
        get.ContentType.Should().StartWith("text/vcard");
        get.Body.Should().Contain("VERSION:3.0").And.Contain("UID:p-2");
        get.Headers["ETag"].Should().Be("\"v1\"");
        notModified.Status.Should().Be(304);
        invisible.Status.Should().Be(404);
    }

    [Fact]
    public async Task AVisibilityChange_ChangesTheCTag_WithoutAnyPublish() {
        await using var harness = Create();
        var before = await CTagAsync(harness);

        Directory(harness).Contacts["p-3"].Viewers.Add("alice");
        var widened = await CTagAsync(harness);
        Directory(harness).Contacts["p-3"].Viewers.Remove("alice");
        var back = await CTagAsync(harness);

        widened.Should().NotBe(before);
        back.Should().Be(before, "the default CTag is a hash of what the viewer sees");
    }

    [Fact]
    public async Task SyncCollection_IsRefusedWithSupportedReport() {
        await using var harness = Create();

        var response = await harness.SendAsync("REPORT", Book, CardDavTestHarness.SyncCollection(null));

        response.Status.Should().Be(403);
        response.Body.Should().Contain("supported-report");
    }

    [Fact]
    public async Task PublishingIntoALiveSource_Throws() {
        await using var harness = Create();

        var publish = () => harness.PublishAsync("people", "all", CardDavTestData.Person("p-9", "Edsger", "Dijkstra"));

        (await publish.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("live");
    }

    [Fact]
    public async Task ALiveContactWithAnInvalidName_FailsAsTheApplicationsMistake() {
        await using var harness = CardDavTestHarness.Create(services => {
            services.AddScoped<IHandler<PeopleAddressBooks, Result<CardDavSourceListing>>, ListPeopleAddressBooks>();
            services.AddElarionCardDavLiveSource<PeopleAddressBooks, BrokenPeople>();
        });

        var listing = () => harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("D:getetag"), headers: Depth("1"));

        await listing.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task AnApplicationWithOnlyLiveAddressBooks_NeedsNoStore_AndPassesValidateOnBuild() {
        var services = new ServiceCollection();
        services.AddElarionCardDav();
        services.AddSingleton<LiveContactDirectory>();
        services.AddScoped<TestUserAccessor>();
        services.AddScoped<ICurrentUser>(provider => provider.GetRequiredService<TestUserAccessor>().User);
        services.AddScoped<IHandler<PeopleAddressBooks, Result<CardDavSourceListing>>, ListPeopleAddressBooks>();
        services.AddElarionCardDavLiveSource<PeopleAddressBooks, VisiblePeople>();

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        provider.GetRequiredService<LiveContactDirectory>().Add(CardDavTestData.Person("p-1", "Ada", "Lovelace"), "alice");
        await using var scope = provider.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestUserAccessor>().User = new TestDavUser("alice");
        var response = await scope.ServiceProvider.GetRequiredService<DavDispatcher>().HandleAsync(new DavRequest {
            Method = "PROPFIND", Path = Book, HrefBase = "/dav",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Depth"] = "1" },
            Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CardDavTestHarness.PropFind("D:getetag"))),
        }, scope.ServiceProvider, TestContext.Current.CancellationToken);
        var publisher = scope.ServiceProvider.GetRequiredService<ICardDavPublisher>();
        var publish = async () => await publisher.PublishAsync(new CardDavAddress("people", "all"), CardDavTestData.Person("p-9", "Edsger", "Dijkstra"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(207);
        (await publish.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("live");
    }

    [Fact]
    public async Task PublishingWithoutAStore_SaysWhichStoreToRegister() {
        var services = new ServiceCollection();
        services.AddElarionCardDav();
        services.AddSingleton<CardDavTestDirectory>();
        services.AddScoped<ICurrentUser>(_ => new TestDavUser("alice"));
        services.AddScoped<IHandler<CustomerAddressBooks, Result<CardDavSourceListing>>, ListCustomerAddressBooks>();
        services.AddElarionCardDavSource<CustomerAddressBooks>();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using var scope = provider.CreateAsyncScope();
        var publisher = scope.ServiceProvider.GetRequiredService<ICardDavPublisher>();

        var publish = async () => await publisher.PublishAsync(new CardDavAddress("customers", "all"), CardDavTestData.Person("c-1", "Ada", "Lovelace"),
            TestContext.Current.CancellationToken);

        (await publish.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("IDavCollectionStore");
    }

    [Fact]
    public async Task ALiveSourceMayGiveItsOwnPropertyList_WrittenInItsOrderAndSpelling() {
        await using var harness = CardDavTestHarness.Create(services => {
            services.AddScoped<IHandler<PeopleAddressBooks, Result<CardDavSourceListing>>, ListPeopleAddressBooks>();
            services.AddElarionCardDavLiveSource<PeopleAddressBooks, OwnCards>();
        });

        var get = await harness.SendAsync("GET", Book + "own.vcf");
        var multiget = await harness.SendAsync("REPORT", Book, CardDavTestHarness.Multiget("<CARD:address-data/>", Href + "own.vcf"));

        get.Status.Should().Be(200);
        get.Body.Should().Be("BEGIN:VCARD\r\nVERSION:3.0\r\nUID:own-1\r\nEMAIL;type=INTERNET,pref:ada@example.com\r\nFN:Ada Lovelace\r\n"
            + "X-SOURCE:crm\r\nEND:VCARD\r\n");
        get.Headers["ETag"].Should().Be("\"e1\"");
        multiget.Prop(Href + "own.vcf", DavNames.Card.AddressData)!.Text.Replace("\r\n", "\n").Should().Be(get.Body.Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task ALivePropertyListWithoutAUid_FailsAsTheApplicationsMistake() {
        await using var harness = CardDavTestHarness.Create(services => {
            services.AddScoped<IHandler<PeopleAddressBooks, Result<CardDavSourceListing>>, ListPeopleAddressBooks>();
            services.AddElarionCardDavLiveSource<PeopleAddressBooks, OwnCards>();
        });

        var get = () => harness.SendAsync("GET", Book + "broken.vcf");

        (await get.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("UID");
    }

    [Fact]
    public void TheDefaultCTag_IsAHashOfTheListingInNameOrder() {
        var a = CardDavLiveMember.ComputeCTag([new("a.vcf", "1"), new("b.vcf", "2")]);
        var reordered = CardDavLiveMember.ComputeCTag([new("b.vcf", "2"), new("a.vcf", "1")]);
        var changed = CardDavLiveMember.ComputeCTag([new("a.vcf", "1"), new("b.vcf", "3")]);

        reordered.Should().Be(a);
        changed.Should().NotBe(a);
        a.Should().StartWith("\"").And.EndWith("\"");
    }

    private static async Task<string> CTagAsync(CardDavTestHarness harness) =>
        (await harness.SendAsync("PROPFIND", Book, CardDavTestHarness.PropFind("CS:getctag"), headers: Depth("0"))).Prop(Href, GetCTag)!.Text;

    private static Dictionary<string, string> Depth(string value) => new() { ["Depth"] = value };

    internal sealed class BrokenPeople : ICardDavLiveAddressBooks {
        public async IAsyncEnumerable<CardDavLiveMember> ListAsync(string key, [EnumeratorCancellation] CancellationToken cancellationToken) {
            yield return new CardDavLiveMember("a/b.vcf", "1");
            await Task.CompletedTask;
        }

        public IAsyncEnumerable<CardDavLiveContact> GetAsync(string key, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
            CancellationToken cancellationToken) => AsyncEnumerable.Empty<CardDavLiveContact>();
    }

    /// <summary>A live source that writes its own cards: its order, its spelling, a property of its own.</summary>
    internal sealed class OwnCards : ICardDavLiveAddressBooks {
        private static readonly IReadOnlyList<ContactProperty> Own = [
            new("UID", "own-1"),
            new(null, "EMAIL", [new ContactParameter("type", ["INTERNET", "pref"])], "ada@example.com"),
            new("FN", "Ada Lovelace"),
            new("X-SOURCE", "crm"),
        ];

        public async IAsyncEnumerable<CardDavLiveMember> ListAsync(string key, [EnumeratorCancellation] CancellationToken cancellationToken) {
            yield return new CardDavLiveMember("own.vcf", "e1");
            yield return new CardDavLiveMember("broken.vcf", "e2");
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<CardDavLiveContact> GetAsync(string key, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
            [EnumeratorCancellation] CancellationToken cancellationToken) {
            yield return new CardDavLiveContact("own.vcf", "e1", Own);
            yield return new CardDavLiveContact("broken.vcf", "e2", [new ContactProperty("FN", "No Uid")]);
            await Task.CompletedTask;
        }
    }
}
