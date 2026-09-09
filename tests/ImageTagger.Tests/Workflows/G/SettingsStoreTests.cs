using System.Text.Json;
using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Settings;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Missing_file_returns_complete_defaults()
    {
        using var temp = new TempDirectory("settings-defaults");
        var settings = new SettingsStore(temp.FullPath).Load();

        Assert.Equal(new AppSettings(), settings);
    }

    [Fact]
    public void Save_and_load_round_trip_all_application_settings()
    {
        using var temp = new TempDirectory("settings-roundtrip");
        var store = new SettingsStore(temp.FullPath);
        var expected = new AppSettings
        {
            ModelPackPath = "custom-pack.v2",
            Theme = ThemePreference.Dark,
            UiLanguage = "zh-CN",
            RecursiveFolderScan = true,
            RestoreWindowLayout = false,
            ShowChineseTranslation = false,
            Acceleration = AccelerationPreference.PowerSaver,
            WindowLayout = new WindowLayoutSettings
            {
                WindowWidth = 1280,
                WindowHeight = 760,
                IsMaximized = true,
                IsLeftPaneExpanded = false,
                LeftPaneWidth = 310,
                IsRightPaneExpanded = false,
                RightPaneWidth = 410,
            },
        };

        store.Save(expected);

        Assert.Equal(expected, store.Load());
        var json = File.ReadAllText(Path.Combine(temp.FullPath, SettingsStore.FileName));
        Assert.Contains($"\"schemaVersion\": {SettingsStore.CurrentSchemaVersion}", json, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(temp.FullPath, "*.tmp"));
    }

    [Fact]
    public void Pre_versioned_settings_are_migrated_in_memory()
    {
        using var temp = new TempDirectory("settings-v0");
        temp.WriteFile(SettingsStore.FileName, """
            {
              "modelPackPath": "legacy-pack",
              "theme": "light",
              "uiLanguage": "zh-CN",
              "recursiveFolderScan": true,
              "restoreWindowLayout": true,
              "showChineseTranslation": true,
              "acceleration": "cpuOnly",
              "windowLayout": {
                "windowWidth": 1120,
                "windowHeight": 720,
                "isMaximized": false,
                "isLeftPaneExpanded": true,
                "leftPaneWidth": 286,
                "isRightPaneExpanded": true,
                "rightPaneWidth": 360
              }
            }
            """u8.ToArray());

        var settings = new SettingsStore(temp.FullPath).Load();

        Assert.Equal("legacy-pack", settings.ModelPackPath);
        Assert.Equal(ThemePreference.Light, settings.Theme);
        Assert.Equal(AccelerationPreference.CpuOnly, settings.Acceleration);
        Assert.Equal(1120, settings.WindowLayout.WindowWidth);
        Assert.Single(Directory.EnumerateFiles(temp.FullPath));
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":99,\"settings\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"settings\":{\"unknown\":true}}")]
    public void Invalid_or_unsupported_settings_are_preserved_and_defaults_restored(string content)
    {
        using var temp = new TempDirectory("settings-corrupt");
        temp.WriteFile(SettingsStore.FileName, System.Text.Encoding.UTF8.GetBytes(content));

        var settings = new SettingsStore(temp.FullPath).Load();

        Assert.Equal(new AppSettings(), settings);
        Assert.False(File.Exists(Path.Combine(temp.FullPath, SettingsStore.FileName)));
        var backup = Assert.Single(Directory.EnumerateFiles(temp.FullPath, "settings.corrupt-*.json"));
        Assert.Equal(content, File.ReadAllText(backup));
    }

    [Fact]
    public void Failed_save_does_not_modify_previous_valid_file()
    {
        using var temp = new TempDirectory("settings-failure");
        var store = new SettingsStore(temp.FullPath);
        var original = new AppSettings { ModelPackPath = "known-good" };
        store.Save(original);
        var originalBytes = File.ReadAllBytes(Path.Combine(temp.FullPath, SettingsStore.FileName));

        var invalid = original with
        {
            WindowLayout = original.WindowLayout with { RightPaneWidth = double.NaN },
        };
        Assert.Throws<JsonException>(() => store.Save(invalid));

        Assert.Equal(originalBytes, File.ReadAllBytes(Path.Combine(temp.FullPath, SettingsStore.FileName)));
        Assert.Equal(original, store.Load());
    }

    [Fact(Skip = "ModelPackId was removed from the active settings model; model paths are now stored explicitly.")]
    public void Model_pack_path_is_never_accepted_as_an_id()
    {
        using var temp = new TempDirectory("settings-path");
        var store = new SettingsStore(temp.FullPath);
        var invalid = new AppSettings { ModelPackPath = string.Concat("folder", Path.DirectorySeparatorChar, "model") };

        Assert.Throws<JsonException>(() => store.Save(invalid));
        Assert.False(File.Exists(Path.Combine(temp.FullPath, SettingsStore.FileName)));
    }
}
