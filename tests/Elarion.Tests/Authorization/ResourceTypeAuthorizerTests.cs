using AwesomeAssertions;
using Elarion.Abstractions.Authorization;
using Elarion.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Authorization;

public sealed class ResourceTypeAuthorizerTests {
    private static readonly FakeCurrentUser Caller = new() { IsAuthenticated = true };

    private static ResourceAuthorizationContext ContextFor(Type resourceType) {
        return new ResourceAuthorizationContext(
            Caller, resourceType, ResourceTypeDiscriminator.For(resourceType), ResourceOperation.Read, 1);
    }

    [Fact]
    public async Task DefaultBackend_RoutesATypeWithItsOwnCheck_AndDeniesEveryOtherType() {
        var invoices = new StubResourceAuthorizer();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionAuthorization();
        services.AddKeyedScoped<IResourceAuthorizer>(typeof(Invoice), (_, _) => invoices);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var authorizer = scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>();
        var ct = TestContext.Current.CancellationToken;

        (await authorizer.AuthorizeResourceAsync(ContextFor(typeof(Invoice)), ct)).Should().BeTrue();
        (await authorizer.AuthorizeResourceAsync(ContextFor(typeof(Receipt)), ct))
            .Should().BeFalse("a type without a check and without a backend fails closed");
        (await authorizer.AuthorizeResourceAsync(ContextFor(typeof(CreditNote)), ct))
            .Should().BeFalse("a check covers exactly its type, not the types derived from it");
        invoices.Calls.Should().ContainSingle().Which.ResourceType.Should().Be(typeof(Invoice));
    }

    [Fact]
    public async Task DefaultBackend_ReturnsTheTypedChecksDenial() {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddElarionAuthorization();
        services.AddKeyedScoped<IResourceAuthorizer>(typeof(Invoice), (_, _) => new StubResourceAuthorizer(false));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        (await scope.ServiceProvider.GetRequiredService<IResourceAuthorizer>()
                .AuthorizeResourceAsync(ContextFor(typeof(Invoice)), TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public void Find_ReturnsNull_ForAProviderWithoutKeyedServices() {
        ResourceTypeAuthorizer.Find(new PlainProvider(), typeof(Invoice)).Should().BeNull();
    }

    private class Invoice;

    private sealed class CreditNote : Invoice;

    private sealed class Receipt;

    private sealed class PlainProvider : IServiceProvider {
        public object? GetService(Type serviceType) {
            return null;
        }
    }
}
