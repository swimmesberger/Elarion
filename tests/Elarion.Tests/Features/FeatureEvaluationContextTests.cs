using AwesomeAssertions;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class FeatureEvaluationContextTests {
    [Fact]
    public void Services_NeedNotBeSet_AndDefaultToAnEmptyProvider() {
        var context = new FeatureEvaluationContext { UserId = "u-1" };

        context.Services.Should().NotBeNull();
        context.Services.GetService<ICurrentUser>().Should().BeNull();
        var act = () => context.Services.GetRequiredService<ICurrentUser>();
        act.Should().Throw<InvalidOperationException>().WithMessage("*carries no services*ICurrentUser*");
    }

    [Fact]
    public void Services_CanNeverBeNull() {
        var act = () => new FeatureEvaluationContext { Services = null! };

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromScope_CarriesTheScopeItWasBuiltFrom_AndDerivedContextsKeepIt() {
        using var provider = new ServiceCollection()
            .AddSingleton<ICurrentUser>(new FakeCurrentUser { UserId = "u-1", IsAuthenticated = true })
            .BuildServiceProvider();

        var ambient = FeatureEvaluationContext.FromScope(provider);
        var derived = ambient with { UserId = "u-2" };

        ambient.Services.Should().BeSameAs(provider);
        derived.Services.Should().BeSameAs(provider);
        ambient.UserId.Should().Be("u-1");
    }
}
