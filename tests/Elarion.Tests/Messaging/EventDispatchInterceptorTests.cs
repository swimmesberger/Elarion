using AwesomeAssertions;
using Elarion.Messaging.InMemory;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Messaging;

public sealed class EventDispatchInterceptorTests
{
    [Fact]
    public void AddInMemoryIntegrationEventBus_RegistersBothInterceptorsScoped()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddInMemoryIntegrationEventBus();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var interceptors = scope.ServiceProvider.GetServices<IInterceptor>().ToList();

        interceptors.Should().ContainSingle(interceptor => interceptor is EventDispatchSaveChangesInterceptor);
        interceptors.Should().ContainSingle(interceptor => interceptor is EventDispatchTransactionInterceptor);
    }
}
