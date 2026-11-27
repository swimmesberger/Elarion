using AwesomeAssertions;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.WebDav;
using Elarion.WebDav;
using Elarion.WebDav.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>Publishing contacts in the handler's transaction and serving them from several nodes, on the EF store over real PostgreSQL.</summary>
[Trait("Category", "Integration")]
public sealed class PostgreSqlCardDavTests(PostgreSqlDavStoreFixture fixture) : IClassFixture<PostgreSqlDavStoreFixture> {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each test publishes into an address book of its own key, so the tests share the database without seeing each other.
    private CardDavTestHarness Node(string key) {
        var harness = CardDavTestHarness.Create(services => {
            services.AddDbContext<DavStoreIntegrationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
            services.AddElarionWebDavEntityFrameworkCore<DavStoreIntegrationDbContext>();
        });
        harness.Directory.Override = [new PublishedAddressBookInfo(key, "Customers")];
        return harness;
    }

    private static string NewKey() => "k" + Guid.NewGuid().ToString("N")[..12];

    [Fact]
    public async Task GivenACustomerPublishedInItsHandler_WhenTheTransactionCommits_ThenTheContactIsInTheAddressBook_AndWhenItRollsBackItIsNot() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = NewKey();
        await using var harness = Node(key);
        var address = new CardDavAddress("customers", key);
        var path = $"/addressbooks/alice/customers.{key}/";

        await using (var scope = harness.Services.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ICardDavPublisher>().PublishAsync(address, CardDavTestData.Person("rolled-back", "Rolled", "Back"), Ct);
            await transaction.RollbackAsync(Ct);
        }

        await using (var scope = harness.Services.CreateAsyncScope()) {
            var context = scope.ServiceProvider.GetRequiredService<DavStoreIntegrationDbContext>();
            await using var transaction = await context.Database.BeginTransactionAsync(Ct);
            await scope.ServiceProvider.GetRequiredService<ICardDavPublisher>().PublishAsync(address, CardDavTestData.Person("committed", "Com", "Mitted"), Ct);
            (await harness.SendAsync("GET", path + "committed.vcf")).Status.Should().Be(404, "it is not committed yet");
            await transaction.CommitAsync(Ct);
        }

        var sync = await harness.SendAsync("REPORT", path, CardDavTestHarness.SyncCollection(null));
        sync.Responses.Keys.Should().Equal($"/dav{path}committed.vcf");
        sync.SyncToken.Should().EndWith(":1");
    }

    [Fact]
    public async Task GivenTwoNodes_WhenAClientSyncsAlternatelyAgainstBoth_ThenEachAnswerContinuesThePreviousOne() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = NewKey();
        await using var nodeA = Node(key);
        await using var nodeB = Node(key);
        var path = $"/addressbooks/alice/customers.{key}/";
        var href = "/dav" + path;

        await nodeA.PublishAsync("customers", key, CardDavTestData.Person("one", "Anna", "Berger"));
        var first = await nodeB.SendAsync("REPORT", path, CardDavTestHarness.SyncCollection(null));
        await nodeB.PublishAsync("customers", key, CardDavTestData.Person("two", "Bernd", "Huber"));
        var second = await nodeA.SendAsync("REPORT", path, CardDavTestHarness.SyncCollection(first.SyncToken));
        await nodeA.PublisherAsync(p => p.RemoveAsync(new CardDavAddress("customers", key), "one", Ct).AsTask());
        var third = await nodeB.SendAsync("REPORT", path, CardDavTestHarness.SyncCollection(second.SyncToken));

        first.Responses.Keys.Should().Equal(href + "one.vcf");
        second.Responses.Keys.Should().Equal(href + "two.vcf");
        third.Responses[href + "one.vcf"].Child(DavNames.D.Status)!.Text.Should().Contain("404");
    }

    [Fact]
    public async Task GivenA300ContactAddressBook_WhenReplacedWith299OneChanged_ThenASyncReceivesExactlyOneChangeAndOneRemoval() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = NewKey();
        await using var harness = Node(key);
        var address = new CardDavAddress("customers", key);
        var path = $"/addressbooks/alice/customers.{key}/";
        await harness.PublisherAsync(p => p.ReplaceAsync(address,
            Enumerable.Range(0, 300).Select(static i => CardDavTestData.Person("c" + i, "Given" + i, "Family")), Ct).AsTask());
        var token = (await harness.SendAsync("REPORT", path, CardDavTestHarness.SyncCollection(null))).SyncToken;

        var outcome = await harness.PublisherAsync(p => p.ReplaceAsync(address,
            Enumerable.Range(0, 299).Select(static i => CardDavTestData.Person("c" + i, "Given" + i, i == 7 ? "Changed" : "Family")), Ct).AsTask());
        var delta = await harness.SendAsync("REPORT", path, CardDavTestHarness.SyncCollection(token));

        outcome.Should().Be(new DavReplaceOutcome(0, 1, 1));
        delta.Responses.Keys.Should().BeEquivalentTo("/dav" + path + "c7.vcf", "/dav" + path + "c299.vcf");
    }

    [Fact]
    public async Task AnAddressBookAndACalendarOfOneSourceNameAndKey_AreDistinctCollectionsInTheStore() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = NewKey();
        await using var harness = Node(key);
        await harness.PublishAsync("customers", key, CardDavTestData.Person("c-1", "Anna", "Berger"));

        await using var scope = harness.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IDavCollectionStore>();
        var states = await store.GetStatesAsync([new CardDavAddress("customers", key).ToCollectionAddress(), new DavCollectionAddress("calendar", "customers", key)], Ct);

        states[0].SyncSeq.Should().Be(1);
        states[1].SyncSeq.Should().Be(0);
    }

    [Fact]
    public async Task AddressbookQuery_ParsesTheStoredCardsAndAnswersTheMatches() {
        Assert.SkipUnless(fixture.IsAvailable, fixture.SkipReason);
        var key = NewKey();
        await using var harness = Node(key);
        await harness.PublishAsync("customers", key, CardDavTestData.Person("anna", "Anna", "Berger", "anna@example.com"));
        await harness.PublishAsync("customers", key, new Contact { Uid = "org", FormattedName = "Example GmbH", Kind = ContactKind.Organization });

        var answer = await harness.SendAsync("REPORT", $"/addressbooks/alice/customers.{key}/",
            CardDavTestHarness.Query("<CARD:prop-filter name=\"EMAIL\"><CARD:text-match>@example.com</CARD:text-match></CARD:prop-filter>"));

        answer.Responses.Keys.Should().Equal($"/dav/addressbooks/alice/customers.{key}/anna.vcf");
    }
}
