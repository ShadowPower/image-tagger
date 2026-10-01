using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.U;

/// <summary>
/// U-01/U-02: 主窗口组合与统一状态流（headless，不启动 UI）。
/// 验证切图时标签/Prompt 原子切换、阈值变化不清除手动排除。
/// </summary>
[Trait("Category", TestCategories.Unit)]
public sealed class MainViewModelCompositionTests
{
    [Fact]
    public void Selection_change_atomically_switches_tags_and_prompt()
    {
        var catalog = FakeModelPack.Catalog();
        var descriptor = FakeModelPack.Descriptor();
        var settings = PromptSettingsFactory.CreateDefault();
        var viewModel = new MainViewModel();
        viewModel.SetModelContext(catalog, descriptor.Groups, settings, descriptor.DefaultThreshold);

        var first = new ImageDocument
        {
            Id = "img-1",
            CanonicalPath =TempImagePath("img-1.png"),
            FileName = "img-1.png",
            FileSize = 10,
            Format = "png",
            PixelWidth = 8,
            PixelHeight = 8,
            Prediction = new PredictionSnapshot(
                FakeModelPack.Fingerprint().Value,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(5),
                "ORT",
                "CPU",
                1,
                [0.9f, 0.1f, 0.8f, 0.7f, 0.2f, 0.05f]),
        };
        var second = new ImageDocument
        {
            Id = "img-2",
            CanonicalPath =TempImagePath("img-2.png"),
            FileName = "img-2.png",
            FileSize = 10,
            Format = "png",
            PixelWidth = 8,
            PixelHeight = 8,
            Prediction = new PredictionSnapshot(
                FakeModelPack.Fingerprint().Value,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(5),
                "ORT",
                "CPU",
                1,
                [0.05f, 0.95f, 0.1f, 0.2f, 0.9f, 0.85f]),
        };
        viewModel.AddImported(new ImageImportResult([first, second], [], 0));
        viewModel.Tags.Threshold = 0.5;
        viewModel.SyncChildViewModels();

        Assert.True(viewModel.SelectById("img-1"));
        viewModel.SyncChildViewModels();
        viewModel.Prompt.Flush();
        var promptForFirst = viewModel.Prompt.PromptText;
        Assert.Contains("sunny", promptForFirst);

        Assert.True(viewModel.SelectById("img-2"));
        viewModel.SyncChildViewModels();
        viewModel.Prompt.Flush();
        var promptForSecond = viewModel.Prompt.PromptText;
        Assert.Contains("night", promptForSecond);
        Assert.DoesNotContain("sunny", promptForSecond);
    }

    [Fact]
    public void Threshold_change_keeps_manual_excludes()
    {
        var catalog = FakeModelPack.Catalog();
        var descriptor = FakeModelPack.Descriptor();
        var viewModel = new MainViewModel();
        viewModel.SetModelContext(catalog, descriptor.Groups, PromptSettingsFactory.CreateDefault(), descriptor.DefaultThreshold);

        var document = new ImageDocument
        {
            Id = "img-1",
            CanonicalPath =TempImagePath("img-1.png"),
            FileName = "img-1.png",
            FileSize = 10,
            Format = "png",
            PixelWidth = 8,
            PixelHeight = 8,
            Prediction = new PredictionSnapshot(
                FakeModelPack.Fingerprint().Value,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(5),
                "ORT",
                "CPU",
                1,
                [0.9f, 0.8f, 0.7f, 0.6f, 0.5f, 0.4f]),
        };
        viewModel.AddImported(new ImageImportResult([document], [], 0));
        viewModel.SyncChildViewModels();

        viewModel.Tags.ToggleExclude(0);
        Assert.Contains(0, viewModel.Tags.Selection.ForceExcludedIndices);

        viewModel.ThresholdPercent = 30;
        Assert.Contains(0, viewModel.Tags.Selection.ForceExcludedIndices);
    }

    [Fact]
    public void Model_context_drives_threshold_from_model_pack()
    {
        var viewModel = new MainViewModel();
        Assert.Equal(60.94, viewModel.ThresholdPercent);

        viewModel.SetModelContext(
            FakeModelPack.Catalog(),
            FakeModelPack.Descriptor().Groups,
            PromptSettingsFactory.CreateDefault(),
            0.5);

        Assert.Equal(50, viewModel.ThresholdPercent);
    }

    [Fact]
    public void Pack_group_thresholds_seed_default_rules_until_user_customizes()
    {
        var groups = new GroupDescriptor[]
        {
            new("general", "General", "通用", DefaultThreshold: 0.2),
            new("character", "Character", "角色", DefaultThreshold: 0.8),
        };

        // 未自定义（工厂默认）时用模型包推荐值播种：进入按分组阈值模式。
        var seeded = new MainViewModel();
        seeded.SetModelContext(FakeModelPack.Catalog(), groups, PromptSettingsFactory.CreateDefault(), 0.5);
        Assert.False(seeded.CurrentPromptSettings.Thresholds.UseGlobalThreshold);

        // 已有用户配置（分组规则非空）时保持用户设置不变。
        var configured = PromptSettingsFactory.CreateDefault() with
        {
            GroupRules = [new GroupRule { GroupId = "general" }],
        };
        var preserved = new MainViewModel();
        preserved.SetModelContext(FakeModelPack.Catalog(), groups, configured, 0.5);
        Assert.True(preserved.CurrentPromptSettings.Thresholds.UseGlobalThreshold);
    }

    private static string TempImagePath(string name) => Path.Combine(Path.GetTempPath(), name);
}
