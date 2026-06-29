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
            Permissions = [Entry("z:read", PermissionKind.Read)],
            Roles = [],
        });
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Billing",
            Permissions = [Entry("invoices:read", PermissionKind.Read), Entry("invoices:write", PermissionKind.Write)],
            Roles = ["accountant"],
        });
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Clients",
            // invoices:read also in Billing -> deduplicated; accountant also in Billing -> deduplicated.
            Permissions = [Entry("invoices:read", PermissionKind.Read), Entry("clients:read", PermissionKind.Read)],
            Roles = ["accountant"],
        });

        var catalog = services.BuildServiceProvider().GetRequiredService<IPermissionCatalog>();

        catalog.Permissions.Should().Equal("clients:read", "invoices:read", "invoices:write", "z:read");
        catalog.Roles.Should().Equal("accountant");
        catalog.Modules.Select(module => module.Module).Should().Equal("Billing", "Catalog", "Clients");
    }

    [Fact]
    public void GroupsByKind() {
        var services = new ServiceCollection();
        services.AddElarionAuthorization();
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Billing",
            Permissions = [
                Entry("invoices:read", PermissionKind.Read),
                Entry("invoices:write", PermissionKind.Write),
                Entry("clients:read", PermissionKind.Read),
            ],
            Roles = [],
        });

        var catalog = services.BuildServiceProvider().GetRequiredService<IPermissionCatalog>();

        catalog.ByKind[PermissionKind.Read].Should().Equal("clients:read", "invoices:read");
        catalog.ByKind[PermissionKind.Write].Should().Equal("invoices:write");
    }

    [Fact]
    public void IsEmptyWhenNoModulesContribute() {
        var catalog = new ServiceCollection()
            .AddElarionAuthorization()
            .BuildServiceProvider()
            .GetRequiredService<IPermissionCatalog>();

        catalog.Permissions.Should().BeEmpty();
        catalog.Roles.Should().BeEmpty();
        catalog.ByKind.Should().BeEmpty();
        catalog.Modules.Should().BeEmpty();
    }

    private static PermissionCatalogEntry Entry(string name, PermissionKind kind) =>
        new() { Name = name, Kind = kind };
}
