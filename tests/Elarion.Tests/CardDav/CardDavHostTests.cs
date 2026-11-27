using System.Net;
using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.AppPasswords;
using Elarion.CalDav;
using Elarion.CardDav;
using Elarion.Contacts;
using Elarion.Tests.CalDav;
using Elarion.WebDav;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>The CalDAV test host with CardDAV and the test address-book sources added: one host serving both.</summary>
internal static class CardDavHost {
    public static Task<CalDavHost> StartAsync(Action<IServiceCollection>? services = null) =>
        CalDavHost.StartAsync(collection => {
            collection.AddElarionCardDav();
            collection.AddSingleton<CardDavTestDirectory>();
            collection.AddScoped<IHandler<CustomerAddressBooks, Result<CardDavSourceListing>>, ListCustomerAddressBooks>();
            collection.AddScoped<IHandler<TeamAddressBooks, Result<CardDavSourceListing>>, ListTeamAddressBooks>();
            collection.AddElarionCardDavSource<CustomerAddressBooks>();
            collection.AddElarionCardDavSource<TeamAddressBooks>();
            services?.Invoke(collection);
        });

    public static async Task PublishContactAsync(this CalDavHost host, string source, string key, Contact contact) {
        await using var scope = host.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ICardDavPublisher>()
            .PublishAsync(new CardDavAddress(source, key), contact, TestContext.Current.CancellationToken);
    }
}

/// <summary>The host serving address books beside calendars (spec 0007 FR-004, FR-016–FR-018, FR-030).</summary>
public sealed class CardDavHostTests {
    private const string Ns = CardDavTestHarness.Namespaces;

    private static string PropFind(params string[] props) => $"<D:propfind {Ns}><D:prop>{string.Concat(props.Select(static p => "<" + p + "/>"))}</D:prop></D:propfind>";

    [Fact]
    public async Task ARequestWithoutCredentials_Answers401WithABasicChallenge() {
        await using var host = await CardDavHost.StartAsync();

        var answer = await host.SendAsync("PROPFIND", "/dav/addressbooks/alice/", PropFind("D:displayname"), depth: "1");

        answer.Status.Should().Be(HttpStatusCode.Unauthorized);
        answer.Headers["WWW-Authenticate"].Should().StartWith("Basic realm=").And.Contain("charset=\"UTF-8\"");
    }

    [Theory]
    [InlineData("PROPFIND")]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task WellKnownCardDav_RedirectsWith301ToThePrefix(string method) {
        await using var host = await CardDavHost.StartAsync();

        var answer = await host.SendAsync(method, "/.well-known/carddav");

        answer.Status.Should().Be(HttpStatusCode.MovedPermanently);
        answer.Headers["Location"].Should().EndWith("/dav/");
    }

    [Fact]
    public async Task Options_NamesAddressbookAndCalendarAccess_AndAllowsNoWrite() {
        await using var host = await CardDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");

        var principal = await host.SendAsync("OPTIONS", "/dav/principals/alice/", user: "alice", password: password);
        var book = await host.SendAsync("OPTIONS", "/dav/addressbooks/alice/customers.all/", user: "alice", password: password);

        principal.Status.Should().Be(HttpStatusCode.OK);
        principal.Headers["DAV"].Should().Be("1, 3, calendar-access, addressbook");
        book.Headers["DAV"].Should().Contain("addressbook");
        book.Headers["Allow"].Should().NotContain("PUT").And.NotContain("DELETE").And.Contain("REPORT");
    }

    [Fact]
    public async Task GivenAHostServingCalendarsAndAddressBooks_WhenOneAppPasswordIsUsed_ThenThePrincipalNamesBothHomesAndBothServe() {
        await using var host = await CardDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishContactAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));

        var principal = await host.SendAsync("PROPFIND", "/dav/principals/alice/", PropFind("CARD:addressbook-home-set").Replace("<D:prop>",
            "<D:prop><C:calendar-home-set xmlns:C=\"urn:ietf:params:xml:ns:caldav\"/>"), "alice@example.com", password, "0");
        var books = await host.SendAsync("PROPFIND", "/dav/addressbooks/alice/", PropFind("D:resourcetype"), "alice@example.com", password, "1");
        var calendars = await host.SendAsync("PROPFIND", "/dav/calendars/alice/", PropFind("D:resourcetype"), "alice@example.com", password, "1");

        principal.Dav.Prop("/dav/principals/alice/", DavNames.Card.AddressBookHomeSet)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/addressbooks/alice/");
        principal.Dav.Prop("/dav/principals/alice/", DavNames.C.CalendarHomeSet)!.Child(DavNames.D.Href)!.Text.Should().Be("/dav/calendars/alice/");
        books.Dav.Responses.Keys.Should().Contain("/dav/addressbooks/alice/customers.all/");
        calendars.Dav.Responses.Keys.Should().Contain("/dav/calendars/alice/shifts.u-alice/");
    }

    [Fact]
    public async Task AFeedToken_DoesNotAuthenticateAnyAddressBookUrl() {
        await using var host = await CardDavHost.StartAsync();
        var feed = await host.CreatePasswordAsync("alice", AppPasswordScope.CalendarFeed(new CalDavAddress("shifts", "u-alice")));

        var answer = await host.SendAsync("PROPFIND", "/dav/addressbooks/alice/", PropFind("D:displayname"), "alice", feed, "1");

        answer.Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetOfAContact_AnswersTextVCardWithItsETag() {
        await using var host = await CardDavHost.StartAsync();
        var password = await host.CreatePasswordAsync("alice");
        await host.PublishContactAsync("customers", "all", CardDavTestData.Person("c-1", "Anna", "Berger"));

        var answer = await host.SendAsync("GET", "/dav/addressbooks/alice/customers.all/c-1.vcf", user: "alice", password: password);

        answer.Status.Should().Be(HttpStatusCode.OK);
        answer.Headers["Content-Type"].Should().Be("text/vcard; charset=utf-8");
        answer.Headers["ETag"].Should().StartWith("\"");
        answer.Dav.Body.Should().StartWith("BEGIN:VCARD\r\nVERSION:3.0\r\n");
    }
}
