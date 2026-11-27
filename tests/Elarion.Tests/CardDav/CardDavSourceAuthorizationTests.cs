using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Abstractions.Pipeline;
using Elarion.Authorization;
using Elarion.CardDav;
using Elarion.Generators;
using Elarion.Pipeline;
using Elarion.Tests.Authorization;
using Elarion.Tests.WebDav;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Elarion.Tests.CardDav;

/// <summary>
/// A source handler's <c>[RequirePermission]</c> is enforced by the handler pipeline although the handler carries no
/// <c>[Handler]</c> (spec 0007 FR-012): the module registration wraps every handler of the module in its decorators,
/// and the CardDAV protocol resolves that registration, so the authorization a source declares is the one that runs.
/// </summary>
public sealed class CardDavSourceAuthorizationTests {
    private const string ModuleSource =
        """
        using System.Threading;
        using System.Threading.Tasks;
        using Elarion.Abstractions;
        using Elarion.Abstractions.Authorization;
        using Elarion.Abstractions.Modules;
        using Elarion.CardDav;

        [assembly: UseElarion]

        namespace Sample.Customers {
            [AppModule("Customers")]
            public static class CustomersModule { }

            public sealed record CustomerAddressBooks : ICardDavSource<CustomerAddressBooks> {
                public static string SourceName => "customers";
            }

            [RequirePermission("customers", "read")]
            public sealed class ListCustomerAddressBooks : IHandler<CustomerAddressBooks, Result<CardDavSourceListing>> {
                public ValueTask<Result<CardDavSourceListing>> HandleAsync(CustomerAddressBooks query, CancellationToken ct) =>
                    ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([new PublishedAddressBookInfo("all", "Customers")])));
            }
        }
        """;

    [Fact]
    public void SourceHandlerWithoutHandlerAttribute_IsRegisteredInItsModuleBehindItsAuthorization() {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var compilation = CSharpCompilation.Create(
            "CardDavSourceModule",
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

        var registration = trees["Sample_Customers_ListCustomerAddressBooks.g.cs"];
        registration.Should().Contain("global::Elarion.Pipeline.AuthorizationDecorator<global::Sample.Customers.CustomerAddressBooks");
        // The module's registration includes the source handler, so enabling the module enables the source.
        trees["CustomersHandlerExtensions.g.cs"].Should().Contain("ListCustomerAddressBooksRegistration.AddListCustomerAddressBooks(services)");
    }

    [Fact]
    public async Task SourceGuardedByThePipelinesAuthorization_ShowsItsAddressBookOnlyToAUserWithThePermission() {
        await using var harness = CardDavTestHarness.Create(services => {
            // As the generated module registration does: the handler behind the authorization decorator, with the
            // requirements read from the handler's own attributes.
            services.AddScoped<ICurrentUser>(provider => {
                var user = provider.GetRequiredService<TestUserAccessor>().User;
                return new FakeCurrentUser {
                    UserId = user.UserId,
                    IsAuthenticated = true,
                    Claims = user.UserId == "alice" ? [("permission", "customers.read")] : [],
                };
            });
            services.AddScoped<IHandler<CustomerAddressBooks, Result<CardDavSourceListing>>>(provider =>
                new AuthorizationDecorator<CustomerAddressBooks, Result<CardDavSourceListing>>(
                    new UncheckedCustomerSource(provider.GetRequiredService<ICurrentUser>()),
                    new HandlerMetadata(typeof(UncheckedCustomerSource), typeof(CustomerAddressBooks), typeof(Result<CardDavSourceListing>)),
                    new ClaimsAuthorizer(provider.GetRequiredService<ICurrentUser>(), [], new StubResourceAuthorizer(),
                        new AuthorizationOptions(), NullLogger<ClaimsAuthorizer>.Instance)));
        });
        harness.Directory.Teams.Clear();
        harness.Directory.GateOpen = false;
        var depthOne = new Dictionary<string, string> { ["Depth"] = "1" };
        var depthZero = new Dictionary<string, string> { ["Depth"] = "0" };
        var query = CardDavTestHarness.PropFind("D:displayname");

        var allowed = await harness.SendAsync("PROPFIND", "/addressbooks/alice/", query, user: "alice", headers: depthOne);
        var denied = await harness.SendAsync("PROPFIND", "/addressbooks/bob/", query, user: "bob", headers: depthOne);
        var deniedDirect = await harness.SendAsync("PROPFIND", "/addressbooks/bob/customers.u-bob/", query, user: "bob", headers: depthZero);

        allowed.Responses.Keys.Should().Contain("/dav/addressbooks/alice/customers.u-alice/");
        denied.Responses.Keys.Should().NotContain(href => href.Contains("customers.", StringComparison.Ordinal));
        deniedDirect.Status.Should().Be(404);
    }

    private static MetadataReference[] TrustedPlatformReferences() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(SharedMetadataReferences.FromFile)];

    /// <summary>A source that checks nothing itself: only the pipeline's authorization keeps it from a user.</summary>
    [RequirePermission("customers", "read")]
    private sealed class UncheckedCustomerSource(ICurrentUser user) : IHandler<CustomerAddressBooks, Result<CardDavSourceListing>> {
        public ValueTask<Result<CardDavSourceListing>> HandleAsync(CustomerAddressBooks request, CancellationToken ct) =>
            ValueTask.FromResult(Result<CardDavSourceListing>.Success(new CardDavSourceListing([new PublishedAddressBookInfo("u-" + user.UserId, "Customers")])));
    }
}
