using AwesomeAssertions;
using Elarion.Abstractions;
using Elarion.Settings;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsManagerTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TypedSet_ThenGet_RoundTripsViaSourceGenJson() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var widgets = new WidgetSettings { MaxItems = 7, Title = "Inbox" };

        var write = await manager.SetAsync(TestSettings.Widgets, widgets, cancellationToken: Ct);
        var loaded = await manager.GetAsync(TestSettings.Widgets, cancellationToken: Ct);

        write.IsSuccess.Should().BeTrue();
        write.Value.Version.Should().Be(1);
        loaded.Should().Be(widgets);
    }

    [Fact]
    public async Task Get_ReturnsTheDeclaredDefault_WhenNothingIsStored() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("Untitled");
        (await manager.GetAsync(TestSettings.Port, cancellationToken: Ct)).Should().Be(25);
        (await manager.GetAsync(TestSettings.Widgets, cancellationToken: Ct)).Should()
            .Be(new WidgetSettings { MaxItems = 3, Title = "default" });
        (await manager.GetAsync(TestSettings.Plain, cancellationToken: Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Reset_RemovesTheStoredValue_AndRevertsToTheDefault() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Title, "Elarion", cancellationToken: Ct);

        var reset = await manager.ResetAsync(TestSettings.Title, cancellationToken: Ct);

        reset.IsSuccess.Should().BeTrue();
        reset.Value.Should().BeTrue();
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("Untitled");
    }

    [Fact]
    public async Task OptimisticWrite_WithStaleVersion_ReturnsTypedConflict() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Title, "a", cancellationToken: Ct);

        var result = await manager.SetAsync(TestSettings.Title, "b", expectedVersion: 5, cancellationToken: Ct);

        result.IsSuccess.Should().BeFalse();
        result.Error.Kind.Should().Be(ErrorKind.Conflict);
        result.Error.Data.Should().Be(new SettingWriteFailure("app:title", SettingWriteFailureReason.ConcurrencyConflict));
        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("a");
    }

    [Fact]
    public async Task UnregisteredDefinition_IsRefused() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var read = async () => await manager.GetAsync(TestSettings.Unregistered, cancellationToken: Ct);
        var write = async () => await manager.SetAsync(TestSettings.Unregistered, "x", cancellationToken: Ct);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not registered*");
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not registered*");
    }

    [Fact]
    public async Task ScopeTheDefinitionDoesNotAllow_IsRefused() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var read = async () => await manager.GetAsync(TestSettings.Title, SettingsScope.User("u1"), Ct);
        var write = async () => await manager.SetAsync(TestSettings.Title, "x", SettingsScope.User("u1"),
            cancellationToken: Ct);

        await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not allow the 'user' scope*");
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not allow the 'user' scope*");
    }

    [Fact]
    public async Task CurrentUserScope_ResolvesOwnerFromCurrentUser() {
        using var provider = SettingsTestHost.Build(
            currentUser: new FakeCurrentUser { IsAuthenticated = true, UserId = "u1" });
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        await manager.SetAsync(TestSettings.Theme, "dark", SettingsScope.CurrentUser, cancellationToken: Ct);

        (await manager.GetAsync(TestSettings.Theme, SettingsScope.User("u1"), Ct)).Should().Be("dark");
        (await manager.GetAsync(TestSettings.Theme, SettingsScope.Global, Ct)).Should().Be("light");
    }

    [Fact]
    public async Task CurrentUserScope_FailsClosed_WhenUnauthenticated() {
        using var provider = SettingsTestHost.Build(currentUser: new FakeCurrentUser { IsAuthenticated = false });
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        var act = async () => await manager.GetAsync(TestSettings.Theme, SettingsScope.CurrentUser, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GlobalScope_WorksWithoutCurrentUserRegistered() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);

        await manager.SetAsync(TestSettings.Title, "Elarion", cancellationToken: Ct);

        (await manager.GetAsync(TestSettings.Title, cancellationToken: Ct)).Should().Be("Elarion");
    }

    [Fact]
    public async Task Watch_FiresAfterWrite() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        var byPrefix = manager.Watch("app");
        var byDefinition = manager.Watch(TestSettings.Title);

        await manager.SetAsync(TestSettings.Title, "Elarion", cancellationToken: Ct);

        byPrefix.HasChanged.Should().BeTrue();
        byDefinition.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Describe_ReportsEffectiveStateForEveryDefinitionAllowingTheScope() {
        using var provider = SettingsTestHost.Build();
        var manager = SettingsTestHost.Scoped<ISettingsManager>(provider);
        await manager.SetAsync(TestSettings.Port, 587, cancellationToken: Ct);

        var descriptions = await manager.DescribeAsync(cancellationToken: Ct);

        descriptions.Select(d => d.Key).Should().NotContain("user:token");
        var port = descriptions.Single(d => d.Key == "app:smtp:port");
        port.Source.Should().Be(SettingSource.Store);
        port.ValueJson.Should().Be("587");
        port.Version.Should().Be(1);
        port.IsPinnable.Should().BeTrue();
        port.IsPinned.Should().BeFalse();
        port.ValueType.Should().Be(typeof(int));

        var title = descriptions.Single(d => d.Key == "app:title");
        title.Source.Should().Be(SettingSource.Default);
        title.HasValue.Should().BeTrue();
        title.ValueJson.Should().Be("\"Untitled\"");
        title.Description.Should().Be("The title.");

        var plain = descriptions.Single(d => d.Key == "app:plain");
        plain.HasValue.Should().BeFalse();
        plain.ValueJson.Should().BeNull();
    }
}
