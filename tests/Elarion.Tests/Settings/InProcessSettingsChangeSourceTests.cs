using AwesomeAssertions;
using Elarion.Settings;
using Elarion.Settings.InProcess;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class InProcessSettingsChangeSourceTests {
    [Fact]
    public void Watch_TokenFires_OnMatchingPublish() {
        var source = new InProcessSettingsChangeSource();
        var token = source.Watch(SettingsScope.Global, "app");

        source.Publish(SettingsScope.Global, "app:title");

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Watch_TokenDoesNotFire_OnDifferentScope() {
        var source = new InProcessSettingsChangeSource();
        var token = source.Watch(SettingsScope.Global, "app");

        source.Publish(SettingsScope.User("u1"), "app:title");

        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void Watch_TokenDoesNotFire_OnNonMatchingPrefix() {
        var source = new InProcessSettingsChangeSource();
        var token = source.Watch(SettingsScope.Global, "app");

        source.Publish(SettingsScope.Global, "other:title");

        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void Watch_TokenDoesNotFire_OnSiblingPrefixWithSharedTextPrefix() {
        var source = new InProcessSettingsChangeSource();
        var token = source.Watch(SettingsScope.Global, "app");

        // "application:x" shares the leading text "app" but is not under the "app" hierarchy node.
        source.Publish(SettingsScope.Global, "application:x");

        token.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void Watch_PrefixMatch_FiresForDescendantKey() {
        var source = new InProcessSettingsChangeSource();
        var token = source.Watch(SettingsScope.Global, "app:smtp");

        source.Publish(SettingsScope.Global, "app:smtp:host");

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Watch_NullPrefix_FiresForAnyKeyInScope() {
        var source = new InProcessSettingsChangeSource();
        var token = source.Watch(SettingsScope.Global);

        source.Publish(SettingsScope.Global, "anything");

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public void Watch_AfterFire_ReturnsFreshUnfiredToken() {
        var source = new InProcessSettingsChangeSource();
        var first = source.Watch(SettingsScope.Global, "app");
        source.Publish(SettingsScope.Global, "app:title");
        first.HasChanged.Should().BeTrue();

        var second = source.Watch(SettingsScope.Global, "app");

        second.HasChanged.Should().BeFalse();
    }
}
