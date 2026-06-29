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
        // Order intentionally not sorted; module C registered first to prove the catalog sorts.
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Catalog",
            Permissions = ["z:read"],
            Roles = [],
        });
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Billing",
            Permissions = ["invoices:read", "invoices:write"],
            Roles = ["accountant"],
        });
        services.AddSingleton(new PermissionCatalogModule {
            Module = "Clients",
            Permissions = ["invoices:read", "clients:read"], // invoices:read also in Billing -> deduplicated
            Roles = ["accountant"], // accountant also in Billing -> deduplicated
        });

        var catalog = services.BuildServiceProvider().GetRequiredService<IPermissionCatalog>();

        catalog.Permissions.Should().Equal("clients:read", "invoices:read", "invoices:write", "z:read");
        catalog.Roles.Should().Equal("accountant");
        catalog.Modules.Select(module => module.Module).Should().Equal("Billing", "Catalog", "Clients");
    }

    [Fact]
    public void IsEmptyWhenNoModulesContribute() {
        var catalog = new ServiceCollection()
            .AddElarionAuthorization()
            .BuildServiceProvider()
            .GetRequiredService<IPermissionCatalog>();

        catalog.Permissions.Should().BeEmpty();
        catalog.Roles.Should().BeEmpty();
        catalog.Modules.Should().BeEmpty();
    }
}
