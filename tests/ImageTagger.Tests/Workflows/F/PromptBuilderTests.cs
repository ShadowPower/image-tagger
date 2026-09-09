using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using Xunit;

namespace ImageTagger.Tests.Workflows.F;

/// <summary>PromptBuilder 纯函数测试：组顺序、阈值、排除、变换、权重、截断、前后缀与确定性。</summary>
[Trait("Category", "Unit")]
public sealed class PromptBuilderTests
{
    private static readonly GroupDescriptor[] ManifestGroups =
    [
        new GroupDescriptor("rating", "Rating", "分级"),
        new GroupDescriptor("character", "Character", "角色"),
        new GroupDescriptor("general", "General", "通用"),
    ];

    [Fact]
    [Trait("Category", "Unit")]
    public void Group_order_follows_group_rules_and_defaults_to_reversed_manifest()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "general", "普通", "rating"),
            new TagCatalogEntry(1, "char_a", "角色A", "character"),
            new TagCatalogEntry(2, "sky", "天空", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.9f, 0.9f]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        // 默认逆序：general → character → rating。
        Assert.Equal(["general", "character", "rating"], settings.GroupRules.Select(r => r.GroupId));

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("sky, char a, general", result.Prompt);
        Assert.Equal(3, result.TagCount);
        Assert.Equal(result.Prompt.Length, result.CharCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Threshold_boundary_is_inclusive()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "keep", "保留", "general"),
            new TagCatalogEntry(1, "drop", "丢弃", "general"),
        ]);
        var prediction = Snapshot([0.5f, 0.4999f]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("keep", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Rating_group_takes_top_item_regardless_of_threshold_unless_disabled()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "general", "普通", "rating"),
            new TagCatalogEntry(1, "sensitive", "敏感", "rating"),
            new TagCatalogEntry(2, "questionable", "存疑", "rating"),
            new TagCatalogEntry(3, "explicit", "明确", "rating"),
            new TagCatalogEntry(4, "sky", "天空", "general"),
        ]);
        // 分级最高为 sensitive（0.8），通用 sky 低于高阈值。
        var prediction = Snapshot([0.1f, 0.8f, 0.3f, 0.2f, 0.4f]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.9, settings, ManifestGroups);
        Assert.Equal("sensitive", result.Prompt);

        var disabled = settings with
        {
            GroupRules = settings.GroupRules.Select(r =>
                r.GroupId == "rating" ? r with { Enabled = false } : r).ToArray(),
        };
        var disabledResult = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.9, disabled, ManifestGroups);
        Assert.Equal(string.Empty, disabledResult.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Manual_exclusion_removes_tag_even_above_threshold()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
            new TagCatalogEntry(1, "cloud", "云", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.8f]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var selection = new TagSelection();
        selection.ForceExcludedIndices.Add(0);

        var result = PromptBuilder.Build(catalog, prediction, selection, 0.5, settings, ManifestGroups);

        Assert.Equal("cloud", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Exclusion_list_matches_exact_original_tag()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "solo", "单人", "general"),
            new TagCatalogEntry(1, "soloist", "独奏者", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.9f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Transforms = baseSettings.Transforms with
            {
                ExcludedTags = ["solo"],
            },
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("soloist", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Exact_replacement_is_applied()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "1girl", "单个女孩", "general"),
        ]);
        var prediction = Snapshot([0.9f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Transforms = baseSettings.Transforms with
            {
                Replacements = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["1girl"] = "one girl",
                },
            },
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("one girl", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Underscore_conversion_can_be_toggled()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "blue_hair", "蓝发", "general"),
        ]);
        var prediction = Snapshot([0.9f]);
        var enabled = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var disabledBase = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var disabled = disabledBase with
        {
            Transforms = disabledBase.Transforms with { UnderscoreToSpace = false },
        };

        Assert.Equal("blue hair", PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, enabled, ManifestGroups).Prompt);
        Assert.Equal("blue_hair", PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, disabled, ManifestGroups).Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Deduplication_ignores_case_and_keeps_first()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "blue_hair", "蓝发", "general"),
            new TagCatalogEntry(1, "Blue_Hair", "蓝发", "general"),
            new TagCatalogEntry(2, "sky", "天空", "general"),
        ]);
        // 第一项概率更高，保证去重保留首次。
        var prediction = Snapshot([0.95f, 0.9f, 0.8f]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("blue hair, sky", result.Prompt);
        Assert.Equal(2, result.TagCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Weight_boundary_keeps_1_00_unwrapped_and_wraps_max()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "low", "低", "general"),
            new TagCatalogEntry(1, "high", "高", "general"),
        ]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Transforms = baseSettings.Transforms with
            {
                ProbabilityWeights = true,
                MinWeight = 1.00,
                MaxWeight = 1.30,
            },
        };

        // 阈值处权重为最小值 1.00，不包裹；满分权重为最大值 1.30，包裹。
        var lowOnly = PromptBuilder.Build(
            catalog, Snapshot([0.5f, 0.0f]), new TagSelection(), 0.5, settings, ManifestGroups);
        Assert.Equal("low", lowOnly.Prompt);

        var highOnly = PromptBuilder.Build(
            catalog, Snapshot([0.0f, 1.0f]), new TagSelection(), 0.5, settings, ManifestGroups);
        Assert.Equal("(high:1.30)", highOnly.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Max_tags_truncates_in_final_group_order()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "a", "甲", "general"),
            new TagCatalogEntry(1, "b", "乙", "general"),
            new TagCatalogEntry(2, "c", "丙", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.8f, 0.7f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Output = baseSettings.Output with { MaxTags = 2 },
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("a, b", result.Prompt);
        Assert.Equal(2, result.TagCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Prefix_and_suffix_are_concatenated_directly()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
        ]);
        var prediction = Snapshot([0.9f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Output = baseSettings.Output with { Prefix = "pre:", Suffix = ":suf" },
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("pre:sky:suf", result.Prompt);
        Assert.Equal(1, result.TagCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Tag_and_group_separators_apply_per_level()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "c1", "角色1", "character"),
            new TagCatalogEntry(1, "g1", "通用1", "general"),
            new TagCatalogEntry(2, "g2", "通用2", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.9f, 0.8f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Output = baseSettings.Output with { TagSeparator = "|", GroupSeparator = ";" },
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        // 默认逆序 general 在前：组内用 |，组间用 ;。
        Assert.Equal("g1|g2;c1", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Per_group_thresholds_apply_when_global_is_disabled()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "c1", "角色1", "character"),
            new TagCatalogEntry(1, "g1", "通用1", "general"),
        ]);
        var prediction = Snapshot([0.6f, 0.6f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Thresholds = new ThresholdRules
            {
                UseGlobalThreshold = false,
                GroupThresholds = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["character"] = 0.9,
                    ["general"] = 0.5,
                },
            },
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("g1", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Name_ascending_sorts_by_original_tag()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "zebra", "斑马", "general"),
            new TagCatalogEntry(1, "apple", "苹果", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.8f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            GroupRules =
            [
                new GroupRule { GroupId = "general", Enabled = true, SortMode = GroupSortMode.NameAscending },
                new GroupRule { GroupId = "character", Enabled = true, SortMode = GroupSortMode.ConfidenceDescending },
                new GroupRule { GroupId = "rating", Enabled = true, SortMode = GroupSortMode.ConfidenceDescending },
            ],
        };

        var result = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal("apple, zebra", result.Prompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Empty_inputs_return_empty_string_and_trybuild_fails()
    {
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);

        Assert.Equal(string.Empty, PromptBuilder.Build(null, null, null, 0.5, settings, ManifestGroups).Prompt);
        Assert.Equal(string.Empty, PromptBuilder.Build(null, null, null, 0.5, null, null).Prompt);

        Assert.False(PromptBuilder.TryBuild(null, null, null, 0.5, settings, ManifestGroups, out var result));
        Assert.Equal(string.Empty, result.Prompt);
        Assert.Equal(0, result.TagCount);
        Assert.Equal(0, result.CharCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Same_inputs_produce_same_outputs()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
            new TagCatalogEntry(1, "cloud", "云", "general"),
        ]);
        var prediction = Snapshot([0.9f, 0.8f]);
        var baseSettings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var settings = baseSettings with
        {
            Transforms = baseSettings.Transforms with { ProbabilityWeights = true },
        };

        var first = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);
        var second = PromptBuilder.Build(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups);

        Assert.Equal(first, second);
        Assert.True(PromptBuilder.TryBuild(catalog, prediction, new TagSelection(), 0.5, settings, ManifestGroups, out var tried));
        Assert.Equal(first, tried);
    }

    private static PredictionSnapshot Snapshot(float[] probabilities) => new(
        "test-fingerprint",
        DateTimeOffset.UnixEpoch,
        TimeSpan.FromMilliseconds(12),
        "test-runtime",
        "cpu",
        1,
        probabilities);
}
