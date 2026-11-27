using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Authorization;
using Elarion.CalDav;
using Elarion.Generators;
using Elarion.Pipeline;
using Elarion.Tests.Authorization;
using Elarion.Tests.WebDav;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.CalDav;

/// <summary>
/// A source handler's <c>[RequirePermission]</c> is enforced by the handler pipeline although the handler carries no
/// <c>[Handler]</c> (spec 0006 FR-011): the module registration wraps every handler of the module in its decorators,
/// and the CalDAV dispatcher resolves that registration, so the authorization a source declares is the one that runs.
/// </summary>
public sealed class CalDavSourceAuthorizationTests {
    private const string ModuleSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;
        using Elarion.CalDav;

        [assembly: UseElarion]

        namespace Sample.Shifts {
            [AppModule("Shifts")]
            public static class ShiftsModule { }

            public sealed record MyShiftCalendars : ICalDavSource<MyShiftCalendars> {
                public static string SourceName => "shifts";
            }

            [RequirePermission("shifts", "read")]
            public sealed class ListMyShiftCalendars : IHandler<MyShiftCalendars, Result<CalDavSourceListing>> {
                public ValueTask<Result<CalDavSourceListing>> HandleAsync(MyShiftCalendars query, CancellationToken ct) =>
                    ValueTask.FromResult(Result<CalDavSourceListing>.Success(new CalDavSourceListing([new PublishedCalendarInfo("mine", "My shifts")])));
            }
        }
        """;

    [Fact]
    public void SourceHandlerWithoutHandlerAttribute_IsRegisteredInItsModuleBehindItsAuthorization() {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "CalDavSourceModule",
            [CSharpSyntaxTree.ParseText(ModuleSource, parseOptions, cancellationToken: TestContext.Current.CancellationToken)],
            TrustedPlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [
                new HandlerRegistrationGenerator().AsSourceGenerator(), new ModuleDefaultServicesGenerator().AsSourceGenerator(),
                new ElarionManifestGenerator().AsSourceGenerator(),
            ],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out var diagnostics, TestContext.Current.CancellationToken);

        diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        output.GetDiagnostics(TestContext.Current.CancellationToken).Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
        // Transport exposure is decided by [Handler] and [HttpEndpoint] alone (covered by the generator's own tests);
        // a source handler carries neither and is reached only through this registration.
        var trees = driver.GetRunResult().GeneratedTrees.ToDictionary(t => Path.GetFileName(t.FilePath), t => t.GetText().ToString());

        var registration = trees["Sample_Shifts_ListMyShiftCalendars.g.cs"];
        registration.Should().Contain("global::Elarion.Pipeline.AuthorizationDecorator<global::Sample.Shifts.MyShiftCalendars");
        // The module's registration includes the source handler, so enabling the module enables the source.
        trees["ShiftsHandlerExtensions.g.cs"].Should().Contain("ListMyShiftCalendarsRegistration.AddListMyShiftCalendars(services)");
    }

    [Fact]
    public async Task SourceGuardedByThePipelinesAuthorization_ShowsItsCalendarOnlyToAUserWithThePermission() {
        await using var harness = CalDavTestHarness.Create(services => {
            // As the generated module registration does: the handler behind the authorization decorator, with the
            // requirements read from the handler's own attributes.
            services.AddScoped<ICurrentUser>(provider => {
                var user = provider.GetRequiredService<TestUserAccessor>().User;
                return new FakeCurrentUser {
                    UserId = user.UserId,
                    IsAuthenticated = true,
                    Claims = user.UserId == "alice" ? [("permission", "shifts.read")] : [],
                };
            });
            services.AddScoped<IHandler<MyShiftCalendars, Result<CalDavSourceListing>>>(provider =>
                new AuthorizationDecorator<MyShiftCalendars, Result<CalDavSourceListing>>(
                    new UncheckedShiftSource(provider.GetRequiredService<ICurrentUser>()),
                    new HandlerMetadata(typeof(UncheckedShiftSource), typeof(MyShiftCalendars), typeof(Result<CalDavSourceListing>)),
                    new ClaimsAuthorizer(provider.GetRequiredService<ICurrentUser>(), [], new StubResourceAuthorizer(),
                        new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance)));
        });
        harness.Directory.Teams.Clear();
        harness.Directory.GateOpen = false;
        var depthOne = new Dictionary<string, string> { ["Depth"] = "1" };
        var depthZero = new Dictionary<string, string> { ["Depth"] = "0" };
        var query = DavTestHarness.PropFind("D:displayname");

        var allowed = await harness.SendAsync("PROPFIND", "/calendars/alice/", query, user: "alice", headers: depthOne);
        var denied = await harness.SendAsync("PROPFIND", "/calendars/bob/", query, user: "bob", headers: depthOne);
        var deniedDirect = await harness.SendAsync("PROPFIND", "/calendars/bob/shifts.u-bob/", query, user: "bob", headers: depthZero);

        allowed.Responses.Keys.Should().Contain("/dav/calendars/alice/shifts.u-alice/");
        denied.Responses.Keys.Should().NotContain(href => href.Contains("shifts.", StringComparison.Ordinal));
        deniedDirect.Status.Should().Be(404);
    }

    private static MetadataReference[] TrustedPlatformReferences() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(SharedMetadataReferences.FromFile)];

    /// <summary>A source that checks nothing itself: only the pipeline's authorization keeps it from a user.</summary>
    [RequirePermission("shifts", "read")]
    private sealed class UncheckedShiftSource(ICurrentUser user) : IHandler<MyShiftCalendars, Result<CalDavSourceListing>> {
        public ValueTask<Result<CalDavSourceListing>> HandleAsync(MyShiftCalendars request, CancellationToken ct) =>
            ValueTask.FromResult(Result<CalDavSourceListing>.Success(new CalDavSourceListing([new PublishedCalendarInfo("u-" + user.UserId, "My shifts")])));
    }
}
