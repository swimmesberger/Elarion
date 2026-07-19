using AwesomeAssertions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Idempotency;
using Elarion.Idempotency;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Idempotency;

public sealed class IdempotencyKeySeedingTests {
    private static ServiceProvider BuildProvider() {
        return new ServiceCollection().AddElarionIdempotency().BuildServiceProvider();
    }

    [Fact]
    public void SeedScope_SeedsKeyFromDispatchContext() {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var context = new DispatchScopeContext();
        context.Set(new IdempotencyKey("k-123"));
        scope.ServiceProvider.SeedScope(context);

        var accessor = scope.ServiceProvider.GetRequiredService<IIdempotencyKeyAccessor>();
        accessor.TryGetKey(out var key).Should().BeTrue();
        key.Should().Be("k-123");
    }

    [Fact]
    public void NoKeyInContext_AccessorReportsNone() {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.SeedScope(DispatchScopeContext.Empty);

        var accessor = scope.ServiceProvider.GetRequiredService<IIdempotencyKeyAccessor>();
        accessor.TryGetKey(out _).Should().BeFalse();
    }

    [Fact]
    public void KeyIsScopedPerCall() {
        using var provider = BuildProvider();

        using (var scope = provider.CreateScope()) {
            var context = new DispatchScopeContext();
            context.Set(new IdempotencyKey("scoped"));
            scope.ServiceProvider.SeedScope(context);
            scope.ServiceProvider.GetRequiredService<IIdempotencyKeyAccessor>().TryGetKey(out var key).Should()
                .BeTrue();
            key.Should().Be("scoped");
        }

        // A fresh scope with no seeding starts clean.
        using var other = provider.CreateScope();
        other.ServiceProvider.GetRequiredService<IIdempotencyKeyAccessor>().TryGetKey(out _).Should().BeFalse();
    }
}
