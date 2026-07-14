using System.Security.Claims;
using AwesomeAssertions;
using Elarion.Abstractions.Dispatch;
using Xunit;

namespace Elarion.Tests.Dispatch;

public sealed class DispatchScopeContextTests {
    [Fact]
    public void SetAndTryGet_RoundTripsValueByType() {
        var context = new DispatchScopeContext();
        var principal = new ClaimsPrincipal();

        context.Set(principal);

        context.TryGet<ClaimsPrincipal>(out var stored).Should().BeTrue();
        stored.Should().BeSameAs(principal);
    }

    [Fact]
    public void TryGet_UncapturedType_ReturnsFalse() {
        var context = new DispatchScopeContext();

        context.TryGet<ClaimsPrincipal>(out var value).Should().BeFalse();
        value.Should().BeNull();
    }

    [Fact]
    public void Empty_Set_Throws() {
        // Empty is one instance shared across every dispatch; a value stored on it (e.g. a ClaimsPrincipal
        // from a fallback path) would leak per-request state globally. It must be frozen.
        var act = () => DispatchScopeContext.Empty.Set(new ClaimsPrincipal());

        act.Should().Throw<InvalidOperationException>().WithMessage("*new DispatchScopeContext*");
        DispatchScopeContext.Empty.TryGet<ClaimsPrincipal>(out _).Should().BeFalse();
    }
}
