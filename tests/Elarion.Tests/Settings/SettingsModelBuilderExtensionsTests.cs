using AwesomeAssertions;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Xunit;

namespace Elarion.Tests.Settings;

public sealed class SettingsModelBuilderExtensionsTests {
    [Fact]
    public void UseElarionSettings_Defaults_MapsElarionSettingsTable() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionSettings();
        var model = modelBuilder.FinalizeModel();

        var setting = model.FindEntityType(typeof(Setting));
        setting.Should().NotBeNull();
        setting!.GetTableName().Should().Be("elarion_settings");
        setting.GetSchema().Should().BeNull();
    }

    [Fact]
    public void UseElarionSettings_CustomTableAndSchema_AreApplied() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionSettings("app_settings", "cache");
        var model = modelBuilder.FinalizeModel();

        var setting = model.FindEntityType(typeof(Setting));
        setting.Should().NotBeNull();
        setting!.GetTableName().Should().Be("app_settings");
        setting.GetSchema().Should().Be("cache");
    }

    [Fact]
    public void UseElarionSettings_SnakeCaseFalse_UsesPascalCaseNames() {
        var modelBuilder = new ModelBuilder(new ConventionSet());
        modelBuilder.UseElarionSettings(snakeCase: false);
        var model = modelBuilder.FinalizeModel();

        var setting = model.FindEntityType(typeof(Setting))!;
        setting.GetTableName().Should().Be("ElarionSettings");
        setting.FindProperty(nameof(Setting.UpdatedOnUtc))!.GetColumnName().Should().Be("UpdatedOnUtc");
        setting.FindProperty(nameof(Setting.Version))!.GetColumnName().Should().Be("Version");
    }
}
