using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class PermissionCatalogTests {
    [Fact]
    public void AddElarionAuthorizationRegistersCatalog() {
        var provider = new ServiceCollection()
            .AddElarionAuthorization()
            .BuildServiceProvider();

        provider.GetService<IPermissionCatalog>().Should().NotBeNull();
    }

    [Fact]
    public void AggregatesModulesDeduplicatedAndSorted() {
        var services = new ServiceCollection();
        services.AddElarionAuthorization();
        // Order intentionally not sorted; "Catalog" registered first to prove the catalog sorts.
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Catalog",
            Permissions = [Entry("widgets", "read")],
            Roles = []
        });
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Billing",
            Permissions = [Entry("invoices", "read"), Entry("invoices", "write")],
            Roles = ["accountant"]
        });
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Clients",
            // invoices.read also in Billing -> deduplicated; accountant also in Billing -> deduplicated.
            Permissions = [Entry("invoices", "read"), Entry("clients", "read")],
            Roles = ["accountant"]
        });

        var catalog = services.BuildServiceProvider().GetRequiredService<IPermissionCatalog>();

        catalog.Permissions.Should().Equal("clients.read", "invoices.read", "invoices.write", "widgets.read");
        catalog.Roles.Should().Equal("accountant");
        catalog.Modules.Select(module => module.Module).Should().Equal("Billing", "Catalog", "Clients");
    }

    [Fact]
    public void GroupsByResourceAndVerb() {
        var services = new ServiceCollection();
        services.AddElarionAuthorization();
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Billing",
            Permissions = [
                Entry("invoices", "read"),
                Entry("invoices", "write"),
                Entry("clients", "read")
            ],
            Roles = []
        });

        var catalog = services.BuildServiceProvider().GetRequiredService<IPermissionCatalog>();

        catalog.ByResource["invoices"].Should().Equal("invoices.read", "invoices.write");
        catalog.ByVerb["read"].Should().Equal("clients.read", "invoices.read");
        catalog.ByVerb["write"].Should().Equal("invoices.write");
    }

    [Fact]
    public void IsEmptyWhenNoModulesContribute() {
        var catalog = new ServiceCollection()
            .AddElarionAuthorization()
            .BuildServiceProvider()
            .GetRequiredService<IPermissionCatalog>();

        catalog.Permissions.Should().BeEmpty();
        catalog.Roles.Should().BeEmpty();
        catalog.ByResource.Should().BeEmpty();
        catalog.ByVerb.Should().BeEmpty();
        catalog.Modules.Should().BeEmpty();
    }

    private static PermissionCatalogEntry Entry(string resource, string verb) {
        return new PermissionCatalogEntry { Permission = resource + "." + verb, Resource = resource, Verb = verb };
    }
}
