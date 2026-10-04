using System.Text.Json.Serialization;
using Elarion.Abstractions.Settings;

namespace Elarion.Tests.Settings;

/// <summary>
/// Hand-built equivalents of what the setting-definition generator emits (the test assembly does not run the
/// generator on itself), covering the declaration shapes the runtime has to handle.
/// </summary>
internal static class TestSettings {
    public static readonly SettingDefinition<string> Title = new(
        "app:title", new SettingDefinitionOptions { IsPinnable = true, Description = "The title." }, "Untitled");

    public static readonly SettingDefinition<int> Port = new(
        "app:smtp:port", new SettingDefinitionOptions { IsPinnable = true }, 25);

    public static readonly SettingDefinition<bool> Enabled = new(
        "app:enabled", new SettingDefinitionOptions { IsPinnable = true }, true);

    public static readonly SettingDefinition<WidgetSettings> Widgets = new(
        "app:widgets", new SettingDefinitionOptions { IsPinnable = true },
        static () => new WidgetSettings { MaxItems = 3, Title = "default" });

    public static readonly SettingDefinition<string> ApiKey = new(
        "app:apikey", new SettingDefinitionOptions { IsSecret = true, Description = "The API key." });

    public static readonly SettingDefinition<string> Theme = new(
        "user:theme", new SettingDefinitionOptions { Scopes = ["global", "user"] }, "light");

    public static readonly SettingDefinition<string> UserToken = new(
        "user:token", new SettingDefinitionOptions { Scopes = ["user"], IsSecret = true });

    public static readonly SettingDefinition<string> Plain = new("app:plain", new SettingDefinitionOptions());

    public static readonly SettingDefinition<string> Unregistered = new("app:unregistered", new SettingDefinitionOptions());

    public static IReadOnlyList<SettingDefinition> All =>
        [Title, Port, Enabled, Widgets, ApiKey, Theme, UserToken, Plain];
}

internal sealed record WidgetSettings {
    public int MaxItems { get; init; }

    public string Title { get; init; } = "";
}

[JsonSerializable(typeof(WidgetSettings))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal sealed partial class SettingsTestJsonContext : JsonSerializerContext;
