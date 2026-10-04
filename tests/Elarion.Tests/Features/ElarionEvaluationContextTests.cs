using AwesomeAssertions;
using Elarion.Abstractions.Features;
using Elarion.FeatureFlags.OpenFeature;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Features;

public sealed class ElarionEvaluationContextTests {

    [Fact]
    public void AuthenticatedUser_SetsTargetingKeyUserIdAndGroups() {
        var user = new FeatureEvaluationContext { UserId = "u-42", Roles = ["Admin", "Billing"] };

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
        var user = new FeatureEvaluationContext { };

        var context = ElarionEvaluationContext.Create(user);

        context.TargetingKey.Should().BeNull();
        context.ContainsKey(ElarionEvaluationContext.UserIdKey).Should().BeFalse();
    }

    [Fact]
    public void TenantAndCustomAttributes_AreForwardedAsTargetingAttributes() {
        var user = new FeatureEvaluationContext {
            UserId = "u-1",
            TenantId = "tenant-7",
            Attributes = new Dictionary<string, string> { ["plan"] = "pro" }
        };

        var context = ElarionEvaluationContext.Create(user);

        context.GetValue(ElarionEvaluationContext.TenantIdKey).AsString.Should().Be("tenant-7");
        context.GetValue("plan").AsString.Should().Be("pro");
    }
}
