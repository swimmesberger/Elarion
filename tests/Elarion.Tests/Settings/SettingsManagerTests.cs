using System.Text.Json.Serialization;
using AwesomeAssertions;
using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Elarion.Tests.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsManagerTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider BuildProvider(ICurrentUser? currentUser = null) {
        var services = new ServiceCollection();
        if (currentUser is not null) {
            services.AddSingleton(currentUser);
        }

        services.AddElarionSettings();
        return services.BuildServiceProvider();
    }

    private static ISettingsManager Resolve(ServiceProvider provider) =>
        provider.CreateScope().ServiceProvider.GetRequiredService<ISettingsManager>();

    [Fact]
    public async Task TypedSet_ThenGet_RoundTripsViaSourceGenJson() {
        using var provider = BuildProvider();
        var manager = Resolve(provider);
        var settings = new WidgetSettings { MaxItems = 7, Title = "Inbox" };

        await manager.SetAsync(SettingsTestJsonContext.Default.WidgetSettings, "widgets", settings, cancellationToken: Ct);
        var loaded = await manager.GetAsync(
            SettingsTestJsonContext.Default.WidgetSettings, "widgets", new WidgetSettings(), cancellationToken: Ct);

        loaded.Should().Be(settings);
    }

    [Fact]
    public async Task TypedGet_ReturnsFallback_WhenAbsent() {
        using var provider = BuildProvider();
        var manager = Resolve(provider);
        var fallback = new WidgetSettings { MaxItems = 1, Title = "default" };

        var loaded = await manager.GetAsync(
            SettingsTestJsonContext.Default.WidgetSettings, "missing", fallback, cancellationToken: Ct);

        loaded.Should().Be(fallback);
    }

    [Fact]
    public async Task GlobalScope_WorksWithoutCurrentUserRegistered() {
        using var provider = BuildProvider(currentUser: null);
        var manager = Resolve(provider);

        await manager.SetStringAsync("app:title", "Elarion", cancellationToken: Ct);

        (await manager.GetStringAsync("app:title", cancellationToken: Ct)).Should().Be("Elarion");
    }

    [Fact]
    public async Task CurrentUserScope_ResolvesOwnerFromCurrentUser() {
        using var provider = BuildProvider(new FakeCurrentUser { IsAuthenticated = true, UserId = "u1" });
        var manager = Resolve(provider);

        await manager.SetStringAsync("theme", "dark", SettingsScope.CurrentUser, cancellationToken: Ct);

        // Written under the resolved per-user scope, not global.
        (await manager.GetStringAsync("theme", SettingsScope.User("u1"), cancellationToken: Ct)).Should().Be("dark");
        (await manager.GetStringAsync("theme", SettingsScope.Global, cancellationToken: Ct)).Should().BeNull();
    }

    [Fact]
    public async Task CurrentUserScope_FailsClosed_WhenUnauthenticated() {
        using var provider = BuildProvider(new FakeCurrentUser { IsAuthenticated = false });
        var manager = Resolve(provider);

        Func<Task> act = async () =>
            await manager.GetStringAsync("theme", SettingsScope.CurrentUser, cancellationToken: Ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Watch_FiresAfterWrite() {
        using var provider = BuildProvider();
        var manager = Resolve(provider);
        var token = manager.Watch("app");

        await manager.SetStringAsync("app:title", "Elarion", cancellationToken: Ct);

        token.HasChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_DeletesValue() {
        using var provider = BuildProvider();
        var manager = Resolve(provider);
        await manager.SetStringAsync("k", "v", cancellationToken: Ct);

        var removed = await manager.RemoveAsync("k", cancellationToken: Ct);

        removed.Should().BeTrue();
        (await manager.GetStringAsync("k", cancellationToken: Ct)).Should().BeNull();
    }
}

internal sealed record WidgetSettings {
    public int MaxItems { get; init; }

    public string Title { get; init; } = "";
}

[JsonSerializable(typeof(WidgetSettings))]
internal sealed partial class SettingsTestJsonContext : JsonSerializerContext;
