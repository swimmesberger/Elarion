using System.Runtime.CompilerServices;
using System.Text;
using AwesomeAssertions;
using Elarion.WebDav;
using Xunit;

namespace Elarion.Tests.WebDav;

/// <summary>
/// A live collection in the WebDAV core: members read per request from <see cref="IDavLiveCollection"/>, answered by a
/// CTag, without a sync token or <c>sync-collection</c> (ADR-0113, amendment 2026-11-27).
/// </summary>
public sealed class DavLiveCollectionTests {
    private const string Collection = "/dav/things/u1/live/";
    private static readonly DavName GetCTag = new(DavNames.CalendarServer, "getctag");

    private static Dictionary<string, string> Depth(string value) => new() { ["Depth"] = value };

    private static (DavTestHarness Harness, TestLiveCollection Live) Create() {
        var harness = DavTestHarness.Create();
        var live = new TestLiveCollection();
        live.Members["a.txt"] = "alpha";
        live.Members["b.txt"] = "beta";
        harness.Protocol.Visible["u1"] = [new DavCollection(TestThingsProtocol.Address("src", "live"), "Live") { Segment = "live", Live = live }];
        return (harness, live);
    }

    [Fact]
    public async Task TheHome_ListsTheLiveCollectionWithItsCTag_AndWithoutASyncTokenOrSyncCollection() {
        var (harness, live) = Create();
        await using var _ = harness;

        var response = await harness.SendAsync("PROPFIND", "/things/u1/",
            DavTestHarness.PropFind("CS:getctag", "D:sync-token", "D:supported-report-set"), headers: Depth("1"));

        response.Status.Should().Be(207);
        response.Prop(Collection, GetCTag)!.Text.Should().Be(live.CTag);
        response.IsMissing(Collection, DavNames.D.SyncToken).Should().BeTrue();
        response.Prop(Collection, DavNames.D.SupportedReportSet)!.Body().Should().NotContain("sync-collection");
    }

    [Fact]
    public async Task TheCollectionWithDepth1_ListsTheMembersFromTheLiveSource_WithoutContentLengthWhenUnknown() {
        var (harness, live) = Create();
        await using var _ = harness;

        var response = await harness.SendAsync("PROPFIND", "/things/u1/live/", DavTestHarness.PropFind("D:getetag", "D:getcontentlength"),
            headers: Depth("1"));

        response.Responses.Keys.Should().BeEquivalentTo(Collection, Collection + "a.txt", Collection + "b.txt");
        response.Prop(Collection + "a.txt", DavNames.D.GetETag)!.Text.Should().Be(TestLiveCollection.ETagOf("alpha"));
        response.IsMissing(Collection + "a.txt", DavNames.D.GetContentLength).Should().BeTrue();
        live.Purposes.Should().BeEmpty("a listing renders no content");
    }

    [Fact]
    public async Task Get_AsksTheLiveSourceForTheContent_AndHonoursIfNoneMatch() {
        var (harness, live) = Create();
        await using var _ = harness;

        var get = await harness.SendAsync("GET", "/things/u1/live/a.txt");
        var notModified = await harness.SendAsync("GET", "/things/u1/live/a.txt",
            headers: new Dictionary<string, string> { ["If-None-Match"] = TestLiveCollection.ETagOf("alpha") });
        var missing = await harness.SendAsync("GET", "/things/u1/live/c.txt");

        get.Status.Should().Be(200);
        get.Body.Should().Be("alpha");
        get.Headers["ETag"].Should().Be(TestLiveCollection.ETagOf("alpha"));
        notModified.Status.Should().Be(304);
        missing.Status.Should().Be(404);
        live.Purposes.Should().Contain(DavContentPurpose.Get);
    }

    [Fact]
    public async Task PropFindOnAMember_AsksForMetadataOnly() {
        var (harness, live) = Create();
        await using var _ = harness;

        var response = await harness.SendAsync("PROPFIND", "/things/u1/live/b.txt", DavTestHarness.PropFind("D:getetag"), headers: Depth("0"));

        response.Prop(Collection + "b.txt", DavNames.D.GetETag)!.Text.Should().Be(TestLiveCollection.ETagOf("beta"));
        live.Purposes.Should().Equal(DavContentPurpose.Metadata);
    }

    [Fact]
    public async Task SyncCollection_OnALiveCollection_Answers403SupportedReport() {
        var (harness, _) = Create();
        await using var __ = harness;

        var initial = await harness.SendAsync("REPORT", "/things/u1/live/", DavTestHarness.SyncCollection(null));
        var withToken = await harness.SendAsync("REPORT", "/things/u1/live/", DavTestHarness.SyncCollection("urn:elarion:sync:x:1"));

        initial.Status.Should().Be(403);
        initial.Body.Should().Contain("supported-report");
        withToken.Status.Should().Be(403);
    }

    [Fact]
    public async Task WhatTheUserSeesChanges_TheCTagAndTheListingFollow_WithoutAnyPublish() {
        var (harness, live) = Create();
        await using var _ = harness;
        var before = (await harness.SendAsync("PROPFIND", "/things/u1/live/", DavTestHarness.PropFind("CS:getctag"), headers: Depth("0")))
            .Prop(Collection, GetCTag)!.Text;

        live.Members.Remove("b.txt");
        var after = await harness.SendAsync("PROPFIND", "/things/u1/live/", DavTestHarness.PropFind("CS:getctag"), headers: Depth("1"));

        after.Prop(Collection, GetCTag)!.Text.Should().NotBe(before);
        after.Responses.Keys.Should().BeEquivalentTo(Collection, Collection + "a.txt");
    }

    [Fact]
    public async Task AWriteToALiveMember_IsRefusedWithNeedPrivileges() {
        var (harness, _) = Create();
        await using var __ = harness;

        var response = await harness.SendAsync("PUT", "/things/u1/live/a.txt", "changed");

        response.Status.Should().Be(403);
        response.Body.Should().Contain("need-privileges");
    }

    /// <summary>A live collection over a dictionary, recording the purposes it was asked for.</summary>
    internal sealed class TestLiveCollection : IDavLiveCollection {
        public Dictionary<string, string> Members { get; } = new(StringComparer.Ordinal);

        public List<DavContentPurpose> Purposes { get; } = [];

        public string CTag => "\"" + string.Join("|", Members.OrderBy(static m => m.Key, StringComparer.Ordinal).Select(static m => m.Key + "=" + m.Value)) + "\"";

        public static string ETagOf(string text) => DavETag.Compute(Encoding.UTF8.GetBytes(text));

        public ValueTask<string> GetCTagAsync(DavContext context, CancellationToken cancellationToken) => ValueTask.FromResult(CTag);

        public async IAsyncEnumerable<DavMember> ListMembersAsync(DavContext context, [EnumeratorCancellation] CancellationToken cancellationToken) {
            foreach (var (name, text) in Members.OrderBy(static m => m.Key, StringComparer.Ordinal))
                yield return new DavMember { Name = name, ETag = ETagOf(text), ContentType = "text/plain; charset=utf-8" };
            await Task.CompletedTask;
        }

        public async IAsyncEnumerable<DavStoredObject> GetMembersAsync(DavContext context, IReadOnlyCollection<string>? names, DavContentPurpose purpose,
            [EnumeratorCancellation] CancellationToken cancellationToken) {
            Purposes.Add(purpose);
            foreach (var (name, text) in Members) {
                if (names is not null && !names.Contains(name)) continue;
                var bytes = purpose == DavContentPurpose.Metadata ? [] : Encoding.UTF8.GetBytes(text);
                yield return new DavStoredObject {
                    Name = name, Uid = name, ETag = ETagOf(text), ContentType = "text/plain; charset=utf-8", Content = bytes,
                    Size = purpose == DavContentPurpose.Metadata ? null : bytes.Length,
                };
            }

            await Task.CompletedTask;
        }
    }
}
