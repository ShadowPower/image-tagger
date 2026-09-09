using System.Text;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Prompt;
using ImageTagger.Infrastructure.Settings;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.F;

/// <summary>PromptSettingsStore 测试：自动保存、损坏恢复备份、恢复默认不影响 AppSettings。</summary>
[Trait("Category", "Unit")]
public sealed class PromptSettingsStoreTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Missing_file_returns_defaults()
    {
        using var temp = new TempDirectory("prompt-defaults");
        var settings = new PromptSettingsStore(temp.FullPath).Load();

        AssertPromptSettingsEqual(PromptSettingsFactory.CreateDefault(), settings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Flush_save_round_trips_with_camel_case_envelope()
    {
        using var temp = new TempDirectory("prompt-roundtrip");
        using var store = new PromptSettingsStore(temp.FullPath);
        var expected = PromptSettingsFactory.CreateDefault() with
        {
            Output = new OutputRules { TagSeparator = " | ", GroupSeparator = "\n", Prefix = "pre", Suffix = "suf", MaxTags = 42 },
        };

        store.Save(expected, flush: true);

        using var reloaded = new PromptSettingsStore(temp.FullPath);
        AssertPromptSettingsEqual(expected, reloaded.Load());
        var json = File.ReadAllText(Path.Combine(temp.FullPath, PromptSettingsStore.FileName));
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"maxTags\": 42", json, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFiles(temp.FullPath, "*.tmp"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Debounced_save_writes_in_background_without_flush()
    {
        using var temp = new TempDirectory("prompt-debounce");
        using var store = new PromptSettingsStore(temp.FullPath);
        var expected = PromptSettingsFactory.CreateDefault() with
        {
            Output = new OutputRules { TagSeparator = ";", GroupSeparator = ";", MaxTags = 7 },
        };

        store.Save(expected, flush: false);

        // 300ms 防抖：等待后台写落盘后用新实例读取。
        Thread.Sleep(800);
        using var reloaded = new PromptSettingsStore(temp.FullPath);
        AssertPromptSettingsEqual(expected, reloaded.Load());
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("{not-json")]
    [InlineData("{\"schemaVersion\":99,\"settings\":{}}")]
    [InlineData("{\"schemaVersion\":1,\"settings\":{\"output\":{\"tagSeparator\":\"\",\"groupSeparator\":\", \",\"maxTags\":100}}}")]
    [InlineData("{\"schemaVersion\":1,\"settings\":{\"output\":{\"tagSeparator\":\", \",\"groupSeparator\":\", \",\"maxTags\":0}}}")]
    public void Corrupt_file_is_backed_up_and_defaults_restored(string content)
    {
        using var temp = new TempDirectory("prompt-corrupt");
        File.WriteAllBytes(
            Path.Combine(temp.FullPath, PromptSettingsStore.FileName),
            Encoding.UTF8.GetBytes(content));

        using var store = new PromptSettingsStore(temp.FullPath);
        var settings = store.Load();

        AssertPromptSettingsEqual(PromptSettingsFactory.CreateDefault(), settings);
        Assert.False(File.Exists(Path.Combine(temp.FullPath, PromptSettingsStore.FileName)));
        var backup = Assert.Single(Directory.EnumerateFiles(temp.FullPath, "prompt-settings.corrupt-*.json"));
        Assert.Equal(content, File.ReadAllText(backup));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Reset_to_defaults_does_not_touch_app_settings_file()
    {
        using var temp = new TempDirectory("prompt-reset-isolation");
        using var promptStore = new PromptSettingsStore(temp.FullPath);
        var appStore = new SettingsStore(temp.FullPath);

        var appSettings = new AppSettings { ModelPackPath = "keep-me", Theme = ThemePreference.Dark };
        appStore.Save(appSettings);
        var customPrompt = PromptSettingsFactory.CreateDefault() with
        {
            Output = new OutputRules { TagSeparator = ";", GroupSeparator = ";", MaxTags = 5 },
        };
        promptStore.Save(customPrompt, flush: true);

        // “恢复默认”由调用方 Save(Default)+flush 实现。
        promptStore.Save(PromptSettingsFactory.CreateDefault(), flush: true);

        AssertPromptSettingsEqual(PromptSettingsFactory.CreateDefault(), promptStore.Load());
        Assert.Equal(appSettings, appStore.Load());
        Assert.True(File.Exists(Path.Combine(temp.FullPath, SettingsStore.FileName)));
        Assert.True(File.Exists(Path.Combine(temp.FullPath, PromptSettingsStore.FileName)));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(0)]
    [InlineData(501)]
    public void Invalid_max_tags_are_rejected(int maxTags)
    {
        using var temp = new TempDirectory("prompt-validate-maxtags");
        using var store = new PromptSettingsStore(temp.FullPath);
        var baseSettings = PromptSettingsFactory.CreateDefault();
        var invalid = baseSettings with { Output = baseSettings.Output with { MaxTags = maxTags } };

        Assert.Throws<System.Text.Json.JsonException>(() => store.Save(invalid, flush: true));
        Assert.False(File.Exists(Path.Combine(temp.FullPath, PromptSettingsStore.FileName)));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Invalid_weights_are_rejected()
    {
        using var temp = new TempDirectory("prompt-validate-weights");
        using var store = new PromptSettingsStore(temp.FullPath);
        var baseSettings = PromptSettingsFactory.CreateDefault();
        var invalid = baseSettings with
        {
            Transforms = baseSettings.Transforms with { MinWeight = 2.0, MaxWeight = 1.0 },
        };

        Assert.Throws<System.Text.Json.JsonException>(() => store.Save(invalid, flush: true));
    }

    private static void AssertPromptSettingsEqual(PromptSettings expected, PromptSettings actual)
    {
        // 记录类型含集合引用比较，直接 Equal 会误判；此处按内容逐项比较。
        Assert.Equal(expected.GroupRules.Count, actual.GroupRules.Count);
        for (int i = 0; i < expected.GroupRules.Count; i++)
            Assert.Equal(expected.GroupRules[i], actual.GroupRules[i]);
        Assert.Equal(expected.Thresholds.UseGlobalThreshold, actual.Thresholds.UseGlobalThreshold);
        Assert.Equal(expected.Thresholds.GroupThresholds.Count, actual.Thresholds.GroupThresholds.Count);
        foreach (var pair in expected.Thresholds.GroupThresholds)
        {
            Assert.True(actual.Thresholds.GroupThresholds.TryGetValue(pair.Key, out var value));
            Assert.Equal(pair.Value, value);
        }
        Assert.Equal(expected.Transforms.UnderscoreToSpace, actual.Transforms.UnderscoreToSpace);
        Assert.Equal(expected.Transforms.ExcludedTags, actual.Transforms.ExcludedTags);
        Assert.Equal(expected.Transforms.Replacements.Count, actual.Transforms.Replacements.Count);
        foreach (var pair in expected.Transforms.Replacements)
        {
            Assert.True(actual.Transforms.Replacements.TryGetValue(pair.Key, out var value));
            Assert.Equal(pair.Value, value);
        }
        Assert.Equal(expected.Transforms.ProbabilityWeights, actual.Transforms.ProbabilityWeights);
        Assert.Equal(expected.Transforms.MinWeight, actual.Transforms.MinWeight);
        Assert.Equal(expected.Transforms.MaxWeight, actual.Transforms.MaxWeight);
        Assert.Equal(expected.Output, actual.Output);
    }
}
