using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;
using Xunit;

namespace ImageTagger.Tests.Workflows.F;

/// <summary>PromptRulesViewModel 测试：顺序、开关、排序、阈值、变换、输出、重置与保存（内存 fake，不依赖 UI 线程）。</summary>
[Trait("Category", "Unit")]
public sealed class PromptRulesViewModelTests
{
    private static readonly GroupDescriptor[] ManifestGroups =
    [
        new GroupDescriptor("rating", "Rating", "分级"),
        new GroupDescriptor("character", "Character", "角色"),
        new GroupDescriptor("general", "General", "通用"),
    ];

    /// <summary>内存提示词设置存储：记录每次保存调用，防抖保存立即可见。</summary>
    private sealed class InMemoryPromptSettingsStore : IPromptSettingsStore
    {
        public readonly List<(PromptSettings Settings, bool Flush)> Saves = [];

        public PromptSettings Load() =>
            Saves.Count == 0 ? PromptSettingsFactory.CreateDefault() : Saves[^1].Settings;

        public void Save(PromptSettings settings, bool flush = false) => Saves.Add((settings, flush));
    }

    private static PromptRulesViewModel CreateDefault(IPromptSettingsStore? store = null) =>
        new(PromptSettingsFactory.CreateDefault(ManifestGroups), ManifestGroups, store);

    private static List<string> GroupIds(PromptRulesViewModel viewModel) =>
        viewModel.Groups.Select(static row => row.GroupId).ToList();

    [Fact]
    [Trait("Category", "Unit")]
    public void Default_order_is_manifest_reverse_with_display_names()
    {
        var viewModel = CreateDefault();

        Assert.Equal(new[] { "general", "character", "rating" }, GroupIds(viewModel));
        Assert.Equal(new[] { "通用", "角色", "分级" }, viewModel.Groups.Select(static row => row.DisplayName));
        Assert.All(viewModel.Groups, static row => Assert.True(row.Enabled));
        Assert.All(viewModel.Groups, static row => Assert.Equal(GroupSortMode.ConfidenceDescending, row.SortMode));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Null_initial_falls_back_to_factory_defaults()
    {
        var viewModel = new PromptRulesViewModel(null, ManifestGroups);

        Assert.Equal(new[] { "general", "character", "rating" }, GroupIds(viewModel));
        Assert.True(viewModel.UseGlobalThreshold);
        Assert.Equal(", ", viewModel.TagSeparator);
        Assert.Equal(100, viewModel.MaxTags);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Move_group_up_down_reorders_and_current_follows_rows()
    {
        var viewModel = CreateDefault();

        viewModel.MoveGroupDown("general");
        Assert.Equal(new[] { "character", "general", "rating" }, GroupIds(viewModel));

        viewModel.MoveGroupUp("rating");
        Assert.Equal(new[] { "character", "rating", "general" }, GroupIds(viewModel));

        Assert.Equal(GroupIds(viewModel), viewModel.Current.GroupRules.Select(static rule => rule.GroupId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Sort_mode_glyph_follows_sort_mode()
    {
        var viewModel = CreateDefault();
        var row = Assert.Single(viewModel.Groups, r => r.GroupId == "general");

        Assert.Equal("按置信度降序", row.SortModeDescription);
        row.SortMode = GroupSortMode.NameAscending;
        Assert.Equal("按名称升序", row.SortModeDescription);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Move_group_out_of_range_or_missing_is_ignored()
    {
        var viewModel = CreateDefault();
        var before = GroupIds(viewModel);

        viewModel.MoveGroupUp("general");
        viewModel.MoveGroupDown("rating");
        viewModel.MoveGroupUp("unknown");
        viewModel.MoveGroupDown("unknown");
        viewModel.MoveGroupUp(null);
        viewModel.MoveGroupDown(string.Empty);

        Assert.Equal(before, GroupIds(viewModel));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Toggle_group_flips_enabled()
    {
        var viewModel = CreateDefault();

        viewModel.ToggleGroup("general");
        Assert.False(viewModel.Groups[0].Enabled);
        Assert.True(viewModel.Groups[1].Enabled);
        Assert.False(viewModel.Current.GroupRules[0].Enabled);

        viewModel.ToggleGroup("general");
        Assert.True(viewModel.Groups[0].Enabled);

        viewModel.ToggleGroup("unknown");
        Assert.All(viewModel.Groups, static row => Assert.True(row.Enabled));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Set_group_sort_accepts_valid_and_ignores_invalid_input()
    {
        var viewModel = CreateDefault();

        viewModel.SetGroupSort("general|NameAscending");
        Assert.Equal(GroupSortMode.NameAscending, viewModel.Groups[0].SortMode);

        viewModel.SetGroupSortMode("character", GroupSortMode.NameAscending);
        Assert.Equal(GroupSortMode.NameAscending, viewModel.Groups[1].SortMode);

        viewModel.SetGroupSort("general|Bogus");
        viewModel.SetGroupSort("unknown|NameAscending");
        viewModel.SetGroupSort(null);
        viewModel.SetGroupSort(string.Empty);
        viewModel.SetGroupSort("general");
        viewModel.SetGroupSort("|NameAscending");
        viewModel.SetGroupSort("general|");
        viewModel.SetGroupSortMode("unknown", GroupSortMode.ConfidenceDescending);
        viewModel.SetGroupSortMode(null, GroupSortMode.ConfidenceDescending);

        Assert.Equal(GroupSortMode.NameAscending, viewModel.Groups[0].SortMode);
        Assert.Equal(GroupSortMode.NameAscending, viewModel.Groups[1].SortMode);
        Assert.Equal(GroupSortMode.ConfidenceDescending, viewModel.Groups[2].SortMode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Threshold_out_of_range_ignored_and_effective_uses_override_when_global_off()
    {
        var viewModel = CreateDefault();

        viewModel.SetGroupThreshold("general", 0.5);
        Assert.Equal(0.5, viewModel.GroupThresholds["general"]);

        viewModel.SetGroupThreshold("general", -0.1);
        viewModel.SetGroupThreshold("general", 1.5);
        viewModel.SetGroupThreshold("general", double.NaN);
        viewModel.SetGroupThreshold(null, 0.5);
        viewModel.SetGroupThreshold(string.Empty, 0.5);
        Assert.Equal(0.5, viewModel.GroupThresholds["general"]);

        Assert.Equal(0.6094, viewModel.EffectiveThreshold("general", 0.6094));

        viewModel.UseGlobalThreshold = false;
        Assert.Equal(0.5, viewModel.EffectiveThreshold("general", 0.6094));
        Assert.Equal(0.6094, viewModel.EffectiveThreshold("character", 0.6094));

        viewModel.ClearGroupThreshold("general");
        Assert.Empty(viewModel.GroupThresholds);
        Assert.Equal(0.6094, viewModel.EffectiveThreshold("general", 0.6094));

        viewModel.ClearGroupThreshold("general");
        viewModel.ClearGroupThreshold(null);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Excluded_tags_add_remove()
    {
        var viewModel = CreateDefault();

        viewModel.AddExcludedTag("bad_tag");
        viewModel.AddExcludedTag("bad_tag");
        viewModel.AddExcludedTag(null);
        viewModel.AddExcludedTag(string.Empty);
        Assert.Equal(new[] { "bad_tag" }, viewModel.ExcludedTags);
        Assert.Contains("bad_tag", viewModel.Current.Transforms.ExcludedTags);

        viewModel.RemoveExcludedTag("missing");
        viewModel.RemoveExcludedTag(null);
        Assert.Single(viewModel.ExcludedTags);

        viewModel.RemoveExcludedTag("bad_tag");
        Assert.Empty(viewModel.ExcludedTags);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Replacements_add_remove_and_empty_original_ignored()
    {
        var viewModel = CreateDefault();

        viewModel.SetReplacement("blue_hair", "blue hair");
        Assert.Equal("blue hair", viewModel.Replacements["blue_hair"]);

        viewModel.SetReplacement(string.Empty, "x");
        viewModel.SetReplacement(null, "x");
        Assert.Single(viewModel.Replacements);

        viewModel.RemoveReplacement("blue_hair");
        Assert.Empty(viewModel.Replacements);

        viewModel.RemoveReplacement("missing");
        viewModel.RemoveReplacement(null);
        Assert.Empty(viewModel.Replacements);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Underscore_to_space_toggle_reflected_in_current()
    {
        var viewModel = CreateDefault();

        Assert.True(viewModel.UnderscoreToSpace);
        viewModel.UnderscoreToSpace = false;
        Assert.False(viewModel.Current.Transforms.UnderscoreToSpace);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Weights_round_trip_and_min_over_max_clamped()
    {
        var viewModel = CreateDefault();

        viewModel.ProbabilityWeights = true;
        viewModel.MinWeight = 1.00;
        viewModel.MaxWeight = 1.30;
        Assert.Equal(1.00, viewModel.Current.Transforms.MinWeight);
        Assert.Equal(1.30, viewModel.Current.Transforms.MaxWeight);

        viewModel.MaxWeight = 4.0;
        viewModel.MinWeight = 9.0;
        Assert.Equal(4.0, viewModel.MinWeight);
        Assert.Equal(4.0, viewModel.MaxWeight);

        viewModel.MinWeight = 0.1;
        Assert.Equal(0.5, viewModel.MinWeight);
        viewModel.MaxWeight = 99.0;
        Assert.Equal(5.0, viewModel.MaxWeight);

        viewModel.MinWeight = double.NaN;
        viewModel.MaxWeight = double.PositiveInfinity;
        Assert.Equal(0.5, viewModel.MinWeight);
        Assert.Equal(5.0, viewModel.MaxWeight);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Separators_ignore_empty_and_max_tags_clamped()
    {
        var viewModel = CreateDefault();

        viewModel.TagSeparator = "\n";
        Assert.Equal("\n", viewModel.TagSeparator);
        viewModel.TagSeparator = string.Empty;
        Assert.Equal("\n", viewModel.TagSeparator);
        viewModel.GroupSeparator = string.Empty;
        Assert.Equal(", ", viewModel.GroupSeparator);

        viewModel.MaxTags = 0;
        Assert.Equal(1, viewModel.MaxTags);
        viewModel.MaxTags = 501;
        Assert.Equal(500, viewModel.MaxTags);
        viewModel.MaxTags = 42;
        Assert.Equal(42, viewModel.Current.Output.MaxTags);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Prefix_suffix_allow_empty_and_trailing_flag_round_trips()
    {
        var viewModel = CreateDefault();

        viewModel.Prefix = "pre";
        viewModel.Suffix = string.Empty;
        viewModel.TrailingSeparator = true;
        Assert.Equal("pre", viewModel.Current.Output.Prefix);
        Assert.Equal(string.Empty, viewModel.Current.Output.Suffix);
        Assert.True(viewModel.Current.Output.TrailingSeparator);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Reset_defaults_restores_manifest_reverse()
    {
        var viewModel = CreateDefault();

        viewModel.MoveGroupDown("general");
        viewModel.ToggleGroup("rating");
        viewModel.SetGroupSort("general|NameAscending");
        viewModel.TagSeparator = ";";
        viewModel.MaxTags = 5;

        viewModel.ResetDefaults();

        Assert.Equal(new[] { "general", "character", "rating" }, GroupIds(viewModel));
        Assert.All(viewModel.Groups, static row => Assert.True(row.Enabled));
        Assert.All(viewModel.Groups, static row => Assert.Equal(GroupSortMode.ConfidenceDescending, row.SortMode));
        Assert.Equal(", ", viewModel.TagSeparator);
        Assert.Equal(100, viewModel.MaxTags);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Mutations_trigger_save_and_flush_forces_once()
    {
        var store = new InMemoryPromptSettingsStore();
        var viewModel = CreateDefault(store);

        Assert.Empty(store.Saves);

        viewModel.ToggleGroup("general");
        Assert.Single(store.Saves);
        Assert.False(store.Saves[0].Flush);
        Assert.False(store.Saves[0].Settings.GroupRules.First(static rule => rule.GroupId == "general").Enabled);

        viewModel.Flush();
        Assert.Equal(2, store.Saves.Count);
        Assert.True(store.Saves[1].Flush);
    }
}
