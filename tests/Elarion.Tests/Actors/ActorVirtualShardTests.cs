using AwesomeAssertions;
using Elarion.Actors;
using Xunit;

namespace Elarion.Tests.Actors;

public sealed class ActorVirtualShardTests {
    [Fact]
    public void SameActorAndKeyAlwaysUseTheSameShard() {
        var first = ActorVirtualShard.GetShardIndex("Order", "42", 16);

        ActorVirtualShard.GetShardIndex("Order", "42", 16).Should().Be(first);
        ActorVirtualShard.GetShardIndex("Order", "42", 16).Should().BeInRange(0, 15);
    }

    [Fact]
    public void ActorNameIsPartOfThePlacementIdentity() {
        var order = ActorVirtualShard.GetShardIndex("Order", "42", 16);
        var invoice = ActorVirtualShard.GetShardIndex("Invoice", "42", 16);

        // The assertion is about the identity input, not a statistical guarantee that every pair
        // must land in different buckets.
        (order == invoice).Should().BeFalse();
    }

    [Fact]
    public void InvalidShardCountFailsBeforeResolvingAKey() {
        var act = () => ActorVirtualShard.GetShardIndex("Order", "42", 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
