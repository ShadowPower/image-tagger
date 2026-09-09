using System.Diagnostics;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using Xunit;

namespace ImageTagger.Tests.Workflows.D;

/// <summary>TagsViewModel 纯逻辑测试：分组排序、阈值、分级、翻译、空组、性能与选择保留。</summary>
[Trait("Category", "Unit")]
public sealed class TagsViewModelTests
{
    private static readonly GroupDescriptor[] ManifestGroups =
    [
        new GroupDescriptor("rating", "Rating", "分级"),
        new GroupDescriptor("character", "Character", "角色"),
        new GroupDescriptor("general", "General", "通用"),
    ];

    [Fact]
    [Trait("Category", "Unit")]
    public void Groups_sort_by_confidence_descending_with_index_tiebreak()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "low", "低", "general"),
            new TagCatalogEntry(1, "tie_a", "平A", "general"),
            new TagCatalogEntry(2, "tie_b", "平B", "general"),
            new TagCatalogEntry(3, "top", "顶", "general"),
        ]);
        var prediction = Snapshot([0.2f, 0.8f, 0.8f, 0.95f]);
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, prediction, 0.5, new TagSelection(), ManifestGroups);

        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.Equal([3, 1, 2], general.VisibleTags.Select(t => t.Index));
        Assert.Equal(3, viewModel.HitCount);
        Assert.Equal(4, viewModel.TotalCount);
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
        // rating 组无条目时仅 general 参与；阈值边界 == 包含。
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.5f, 0.49f]), 0.5, new TagSelection(), ManifestGroups);

        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.Equal([0], general.VisibleTags.Select(t => t.Index));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Rating_group_shows_only_top_result()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "general", "普通", "rating"),
            new TagCatalogEntry(1, "sensitive", "敏感", "rating"),
            new TagCatalogEntry(2, "questionable", "存疑", "rating"),
            new TagCatalogEntry(3, "explicit", "明确", "rating"),
            new TagCatalogEntry(4, "sky", "天空", "general"),
        ]);
        var viewModel = new TagsViewModel();
        // 高阈值过滤掉通用组；分级只展示最高概率的一项，落选的低置信度分级不再展示。
        viewModel.SetInputs(catalog, Snapshot([0.1f, 0.7f, 0.3f, 0.2f, 0.4f]), 0.9, new TagSelection(), ManifestGroups);

        var rating = Assert.Single(viewModel.Groups, g => g.GroupId == "rating");
        Assert.Equal(1, rating.Count);
        Assert.False(rating.IsEmpty);
        var only = Assert.Single(rating.VisibleTags);
        Assert.Equal(1, only.Index);
        Assert.True(only.IsTopRating);

        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.True(general.IsEmpty);
        Assert.Equal(0, general.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Rating_top_result_tiebreaks_by_model_index()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "general", "普通", "rating"),
            new TagCatalogEntry(1, "sensitive", "敏感", "rating"),
            new TagCatalogEntry(2, "questionable", "存疑", "rating"),
        ]);
        var viewModel = new TagsViewModel();
        // 全部低于阈值：仍只展示最高概率的一项（平分取索引最小），不展示其它低置信度分级。
        viewModel.SetInputs(catalog, Snapshot([0.2f, 0.2f, 0.1f]), 0.9, new TagSelection(), ManifestGroups);

        var rating = Assert.Single(viewModel.Groups, g => g.GroupId == "rating");
        var only = Assert.Single(rating.VisibleTags);
        Assert.Equal(0, only.Index);
        Assert.True(only.IsTopRating);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Missing_translation_shows_placeholder()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "untranslated", null, "general"),
            new TagCatalogEntry(1, "same", "same", "general"),
            new TagCatalogEntry(2, "ok", "好", "general"),
        ]);
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f, 0.9f, 0.9f]), 0.5, new TagSelection(), ManifestGroups);

        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.Equal("暂无翻译", general.VisibleTags.First(t => t.Index == 0).TranslationDisplay);
        Assert.Equal("暂无翻译", general.VisibleTags.First(t => t.Index == 1).TranslationDisplay);
        Assert.Equal("好", general.VisibleTags.First(t => t.Index == 2).TranslationDisplay);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Empty_group_keeps_compact_state_instead_of_hiding()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "char_a", "角色A", "character"),
            new TagCatalogEntry(1, "sky", "天空", "general"),
        ]);
        var groups = new[]
        {
            new GroupDescriptor("rating", "Rating", "分级"),
            new GroupDescriptor("character", "Character", "角色"),
            new GroupDescriptor("general", "General", "通用"),
            new GroupDescriptor("artist", "Artist", "艺术家"),
        };
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f, 0.1f]), 0.5, new TagSelection(), groups);

        // 无结果分组保持紧凑空状态，不隐藏整个组，顺序遵循 manifest。
        Assert.Equal(["rating", "character", "general", "artist"], viewModel.Groups.Select(g => g.GroupId));
        Assert.True(viewModel.Groups.First(g => g.GroupId == "artist").IsEmpty);
        Assert.True(viewModel.Groups.First(g => g.GroupId == "general").IsEmpty);
        Assert.False(viewModel.Groups.First(g => g.GroupId == "character").IsEmpty);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Five_hundred_visible_tags_rebuild_quickly()
    {
        const int count = 600;
        var catalog = new TagCatalog(Enumerable.Range(0, count)
            .Select(i => new TagCatalogEntry(i, $"tag_{i}", null, "general"))
            .ToArray());
        var probabilities = Enumerable.Repeat(0.9f, count).ToArray();
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, Snapshot(probabilities), 0.5, new TagSelection(), ManifestGroups);

        var stopwatch = Stopwatch.StartNew();
        viewModel.UpdateThreshold(0.4);
        stopwatch.Stop();

        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.Equal(count, general.Count);
        // 同步重算应为 100ms 量级；冒烟测试放宽到 2 秒避免 CI 抖动。
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"重算耗时 {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Apply_new_prediction_retains_excludes_by_name()
    {
        var oldCatalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "alpha", "甲", "general"),
            new TagCatalogEntry(1, "beta", "乙", "general"),
            new TagCatalogEntry(2, "gamma", "丙", "general"),
        ]);
        var selection = new TagSelection();
        selection.ForceExcludedIndices.Add(1); // beta
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(oldCatalog, Snapshot([0.9f, 0.9f, 0.9f]), 0.5, selection, ManifestGroups);
        viewModel.ToggleExclude(1); // 再次切换应清除（验证 Toggle 行为）
        Assert.Empty(selection.ForceExcludedIndices);
        selection.ForceExcludedIndices.Add(1);

        var newCatalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "beta", "乙", "general"),
            new TagCatalogEntry(1, "alpha", "甲", "general"),
            new TagCatalogEntry(2, "gamma", "丙", "general"),
        ]);
        viewModel.ApplyNewPrediction(Snapshot([0.9f, 0.9f, 0.9f]), newCatalog, ManifestGroups);

        // beta 从索引 1 移动到索引 0，排除应按名跟随。
        Assert.Equal([0], selection.ForceExcludedIndices);
        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.True(general.VisibleTags.First(t => t.Index == 0).IsExcluded);
        Assert.False(general.VisibleTags.First(t => t.Index == 1).IsExcluded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Threshold_change_does_not_clear_excludes()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
            new TagCatalogEntry(1, "cloud", "云", "general"),
        ]);
        var selection = new TagSelection();
        selection.ForceExcludedIndices.Add(0);
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f, 0.8f]), 0.5, selection, ManifestGroups);

        viewModel.UpdateThreshold(0.7);

        Assert.Equal([0], selection.ForceExcludedIndices);
        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.True(general.VisibleTags.First(t => t.Index == 0).IsExcluded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Model_change_discards_unmatched_old_selection()
    {
        var oldCatalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "old_a", "旧甲", "general"),
            new TagCatalogEntry(1, "old_b", "旧乙", "general"),
        ]);
        var selection = new TagSelection();
        selection.ForceExcludedIndices.Add(0);
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(oldCatalog, Snapshot([0.9f, 0.9f]), 0.5, selection, ManifestGroups);

        var newCatalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "new_a", "新甲", "general"),
            new TagCatalogEntry(1, "new_b", "新乙", "general"),
        ]);
        viewModel.ApplyNewPrediction(Snapshot([0.9f, 0.9f]), newCatalog, ManifestGroups);

        // 旧名在新目录中不存在，直接丢弃，不误用到索引 0 的新标签。
        Assert.Empty(selection.ForceExcludedIndices);
        var general = Assert.Single(viewModel.Groups, g => g.GroupId == "general");
        Assert.All(general.VisibleTags, t => Assert.False(t.IsExcluded));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Summary_exposes_hits_threshold_timing_and_runtime()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
            new TagCatalogEntry(1, "cloud", "云", "general"),
        ]);
        var prediction = new PredictionSnapshot(
            "fp", DateTimeOffset.UnixEpoch, TimeSpan.FromMilliseconds(123),
            "test-runtime", "test-provider", 1, [0.9f, 0.1f]);
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, prediction, 0.5, new TagSelection(), ManifestGroups);

        Assert.Equal(1, viewModel.HitCount);
        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(0.5, viewModel.Threshold);
        Assert.Equal(123, viewModel.InferenceMs, 3);
        Assert.Equal("test-runtime", viewModel.Runtime);
        Assert.Equal("test-provider", viewModel.Provider);
        Assert.Equal("test-provider", viewModel.Device);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Clear_overrides_restores_automatic_selection()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
        ]);
        var selection = new TagSelection();
        selection.ForceExcludedIndices.Add(0);
        var viewModel = new TagsViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f]), 0.5, selection, ManifestGroups);
        Assert.True(viewModel.Groups.First(g => g.GroupId == "general").VisibleTags[0].IsExcluded);

        viewModel.ClearOverrides();

        Assert.Empty(selection.ForceExcludedIndices);
        Assert.False(viewModel.Groups.First(g => g.GroupId == "general").VisibleTags[0].IsExcluded);
    }

    private static PredictionSnapshot Snapshot(float[] probabilities) => new(
        "test-fingerprint",
        DateTimeOffset.UnixEpoch,
        TimeSpan.FromMilliseconds(10),
        "test-runtime",
        "cpu",
        1,
        probabilities);
}
