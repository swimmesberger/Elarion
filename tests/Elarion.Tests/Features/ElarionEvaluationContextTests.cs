using AwesomeAssertions;
using Elarion.FeatureFlags.OpenFeature;
using Elarion.Tests.Authorization;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class ElarionEvaluationContextTests {
    [Fact]
    public void AuthenticatedUser_SetsTargetingKeyUserIdAndGroups() {
        var user = new FakeCurrentUser { IsAuthenticated = true, UserId = "u-42", Roles = ["Admin", "Billing"] };

        var context = ElarionEvaluationContext.Create(user);

        // Standard targeting key for vendor providers, plus the UserId/Groups attributes the MS provider reads.
        context.TargetingKey.Should().Be("u-42");
        context.GetValue(ElarionEvaluationContext.UserIdKey).AsString.Should().Be("u-42");
        context.GetValue(ElarionEvaluationContext.GroupsKey).AsList!
            .Select(value => value.AsString)
            .Should().BeEquivalentTo("Admin", "Billing");
    }

    [Fact]
    public void AnonymousUser_HasNoTargetingKeyOrUserId() {
        var user = new FakeCurrentUser { IsAuthenticated = false };

        var context = ElarionEvaluationContext.Create(user);

        context.TargetingKey.Should().BeNull();
        context.ContainsKey(ElarionEvaluationContext.UserIdKey).Should().BeFalse();
    }
}
