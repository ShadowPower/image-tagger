using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ImageTagger.App;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.Prompt;
using ImageTagger.Tests.TestInfrastructure;
using ShadUI;
using Xunit;

namespace ImageTagger.Tests.Workflows.U;

/// <summary>
/// 主窗口功能可用性回归（F-04/F-05/G-06/D-04）：规则编辑、复制导出、
/// 标签操作、快捷键、主窗口 headless 加载。UI 外观不在此验证。
/// </summary>
[Trait("Category", TestCategories.Unit)]
public sealed class MainWindowFunctionTests
{
    private static MainViewModel CreateViewModel(
        FakePlatformService? platform = null,
        FakeImageImportService? imports = null)
    {
        var viewModel = new MainViewModel(
            platformService: platform,
            imageImportService: imports);
        viewModel.SetModelContext(
            FakeModelPack.Catalog(),
            FakeModelPack.Descriptor().Groups,
            PromptSettingsFactory.CreateDefault(),
            FakeModelPack.Descriptor().DefaultThreshold);
        return viewModel;
    }

    private static ImageDocument Document(string id, string path, float[] probs) => new()
    {
        Id = id,
        CanonicalPath = path,
        FileName = Path.GetFileName(path),
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
            probs),
    };

    private static void AddTwo(MainViewModel viewModel, TempDirectory temp)
    {
        var first = Document("img-1", temp.WriteFile("img-1.png", [1]), [0.9f, 0.1f, 0.8f, 0.7f, 0.2f, 0.05f]);
        var second = Document("img-2", temp.WriteFile("img-2.png", [2]), [0.05f, 0.95f, 0.1f, 0.2f, 0.9f, 0.85f]);
        viewModel.AddImported(new Core.Services.ImageImportResult([first, second], [], 0));
        viewModel.SyncChildViewModels();
        viewModel.Prompt.Flush();
    }

    [Fact]
    public async Task CopyPrompt_copies_text_and_reports_status()
    {
        using var temp = new TempDirectory("main-copy");
        var platform = new FakePlatformService();
        var viewModel = CreateViewModel(platform);
        AddTwo(viewModel, temp);

        Assert.False(string.IsNullOrEmpty(viewModel.Prompt.PromptText));
        await ((IAsyncRelayCommand)viewModel.CopyPromptCommand).ExecuteAsync(null);

        Assert.Equal(viewModel.Prompt.PromptText, platform.ClipboardText);
        Assert.Equal("已复制 Prompt", viewModel.StatusBarText);
    }

    [Fact]
    public async Task CopyPrompt_empty_does_not_touch_clipboard()
    {
        var platform = new FakePlatformService();
        var viewModel = CreateViewModel(platform);

        Assert.True(viewModel.Prompt.IsEmpty);
        Assert.False(viewModel.CopyPromptCommand.CanExecute(null));
        await ((IAsyncRelayCommand)viewModel.CopyPromptCommand).ExecuteAsync(null);

        Assert.Null(platform.ClipboardText);
    }

    [Fact]
    public async Task ExportPrompt_writes_picked_path()
    {
        using var temp = new TempDirectory("main-export");
        var platform = new FakePlatformService();
        var viewModel = CreateViewModel(platform);
        AddTwo(viewModel, temp);

        var picked = Path.Combine(temp.FullPath, "img-1.txt");
        platform.NextSavePath = picked;
        await ((IAsyncRelayCommand)viewModel.ExportPromptCommand).ExecuteAsync(null);

        Assert.True(File.Exists(picked));
        Assert.Equal(viewModel.Prompt.PromptText, File.ReadAllText(picked));
        Assert.Equal("已导出 Prompt 文本", viewModel.StatusBarText);
    }

    [Fact]
    public async Task ExportPrompt_cancel_reports_cancel()
    {
        using var temp = new TempDirectory("main-export-cancel");
        var platform = new FakePlatformService { NextSavePath = null };
        var viewModel = CreateViewModel(platform);
        AddTwo(viewModel, temp);

        await ((IAsyncRelayCommand)viewModel.ExportPromptCommand).ExecuteAsync(null);

        Assert.Equal("导出已取消", viewModel.StatusBarText);
    }

    [Fact]
    public async Task ExportPrompt_refuses_to_overwrite_image()
    {
        using var temp = new TempDirectory("main-export-refuse");
        var platform = new FakePlatformService();
        var viewModel = CreateViewModel(platform);
        AddTwo(viewModel, temp);

        var imagePath = viewModel.SelectedImage!.CanonicalPath;
        platform.NextSavePath = imagePath;
        var before = File.ReadAllBytes(imagePath);
        await ((IAsyncRelayCommand)viewModel.ExportPromptCommand).ExecuteAsync(null);

        Assert.Equal(before, File.ReadAllBytes(imagePath));
    }

    [Fact]
    public void ToggleTag_excludes_tag_from_prompt_only()
    {
        using var temp = new TempDirectory("main-toggle");
        var viewModel = CreateViewModel();
        AddTwo(viewModel, temp);

        Assert.Contains("sunny", viewModel.Prompt.PromptText);
        viewModel.ToggleTagCommand.Execute("0");
        viewModel.Prompt.Flush();

        Assert.DoesNotContain("sunny", viewModel.Prompt.PromptText);
        // 标签结果本身保留，只是退出 Prompt。
        Assert.Equal(6, FakeModelPack.Catalog().Count);
        Assert.Contains(0, viewModel.Tags.Selection.ForceExcludedIndices);
    }

    [Fact]
    public void SelectAll_clears_overrides()
    {
        using var temp = new TempDirectory("main-select-all");
        var viewModel = CreateViewModel();
        AddTwo(viewModel, temp);

        viewModel.ToggleTagCommand.Execute("0");
        Assert.NotEmpty(viewModel.Tags.Selection.ForceExcludedIndices);
        viewModel.SelectAllVisibleTagsCommand.Execute(null);

        Assert.Empty(viewModel.Tags.Selection.ForceExcludedIndices);
        viewModel.Prompt.Flush();
        Assert.Contains("sunny", viewModel.Prompt.PromptText);
    }

    [Fact]
    public void Rules_reorder_flows_to_settings_and_preview()
    {
        using var temp = new TempDirectory("main-rules");
        var viewModel = CreateViewModel();
        AddTwo(viewModel, temp);

        // 默认规则无显式分组顺序时，编辑器按 manifest 逆序补齐（主题在前、分级最后）。
        var before = viewModel.Rules.Groups.Select(r => r.GroupId).ToArray();
        Assert.Equal(["style", "subject"], before);

        viewModel.Rules.MoveGroupUpCommand.Execute("subject");
        var afterRows = viewModel.Rules.Groups.Select(r => r.GroupId).ToArray();
        var afterSettings = viewModel.CurrentPromptSettings.GroupRules.Select(r => r.GroupId).ToArray();

        Assert.Equal(["subject", "style"], afterRows);
        Assert.Equal(["subject", "style"], afterSettings);
    }

    [Fact]
    public void ExcludedTag_add_remove_flows_to_prompt()
    {
        using var temp = new TempDirectory("main-excluded");
        var viewModel = CreateViewModel();
        AddTwo(viewModel, temp);

        viewModel.NewExcludedTag = "sunny";
        Assert.True(viewModel.AddExcludedTagCommand.CanExecute(null));
        viewModel.AddExcludedTagCommand.Execute(null);
        viewModel.Prompt.Flush();

        Assert.Contains("sunny", viewModel.Rules.ExcludedTags);
        Assert.DoesNotContain("sunny", viewModel.Prompt.PromptText);

        viewModel.RemoveExcludedTagCommand.Execute("sunny");
        viewModel.Prompt.Flush();
        Assert.Contains("sunny", viewModel.Prompt.PromptText);
    }

    [Fact]
    public void Replacement_flows_to_prompt()
    {
        using var temp = new TempDirectory("main-replace");
        var viewModel = CreateViewModel();
        AddTwo(viewModel, temp);

        viewModel.NewReplacementFrom = "sunny";
        viewModel.NewReplacementTo = "clear sky";
        viewModel.AddReplacementCommand.Execute(null);
        viewModel.Prompt.Flush();

        Assert.Contains("clear sky", viewModel.Prompt.PromptText);

        viewModel.RemoveReplacementCommand.Execute("sunny");
        viewModel.Prompt.Flush();
        Assert.DoesNotContain("clear sky", viewModel.Prompt.PromptText);
    }

    [Fact]
    public void OpenSettings_raises_event()
    {
        var viewModel = CreateViewModel();
        var raised = false;
        viewModel.SettingsRequested += (_, _) => raised = true;

        viewModel.OpenSettingsCommand.Execute(null);

        Assert.True(raised);
    }

    [Fact]
    public void Tags_reports_result_state()
    {
        var viewModel = CreateViewModel();

        Assert.False(viewModel.Tags.HasResult);
        Assert.True(viewModel.Tags.IsUnrecognized);

        using var temp = new TempDirectory("main-hasresult");
        AddTwo(viewModel, temp);

        Assert.True(viewModel.Tags.HasResult);
        Assert.False(viewModel.Tags.IsUnrecognized);
        Assert.Equal(3, viewModel.Tags.HitCount);
    }

    [Fact]
    public async Task Shortcut_ctrl_shift_o_opens_folder()
    {
        using var temp = new TempDirectory("main-shortcut-folder");
        var platform = new FakePlatformService { NextPickedFolder = temp.FullPath };
        var doc = Document("img-9", temp.WriteFile("img-9.png", [9]), [0.9f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
        var viewModel = new MainViewModel(
            platformService: platform,
            imageImportService: new FakeImageImportService(doc));

        Assert.True(MainWindow.TryHandleSessionKey(
            viewModel, Key.O, KeyModifiers.Control | KeyModifiers.Shift, isTextEditing: false));
        // 异步导入在后台完成；等待运行状态恢复空闲。
        for (var i = 0; i < 100 && viewModel.IsOperationRunning; i++)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Single(viewModel.Images);
    }

    [Fact]
    public void Shortcut_ctrl_alt_c_copies_prompt()
    {
        using var temp = new TempDirectory("main-shortcut-copy");
        var platform = new FakePlatformService();
        var viewModel = CreateViewModel(platform);
        AddTwo(viewModel, temp);

        Assert.True(MainWindow.TryHandleSessionKey(
            viewModel, Key.C, KeyModifiers.Control | KeyModifiers.Alt, isTextEditing: true));
    }

    [Fact]
    public void Shortcut_ctrl_comma_opens_settings()
    {
        var viewModel = CreateViewModel();
        var raised = false;
        viewModel.SettingsRequested += (_, _) => raised = true;

        Assert.True(MainWindow.TryHandleSessionKey(
            viewModel, Key.OemComma, KeyModifiers.Control, isTextEditing: false));
        Assert.True(raised);
    }

    [Fact]
    public void Shortcut_delete_ignored_while_editing_text()
    {
        using var temp = new TempDirectory("main-shortcut-edit");
        var viewModel = CreateViewModel();
        AddTwo(viewModel, temp);

        Assert.False(MainWindow.TryHandleSessionKey(
            viewModel, Key.Delete, KeyModifiers.None, isTextEditing: true));
        Assert.Equal(2, viewModel.Images.Count);
    }
}

public sealed class MainWindowHeadlessLoadTests
{
    private static void EnsureShadTheme()
    {
        if (Application.Current is null)
            return;
        foreach (var style in Application.Current.Styles)
        {
            if (style is ShadTheme)
                return;
        }

        Application.Current.Styles.Add(new ShadTheme());
    }

    [AvaloniaFact]
    public void MainWindow_shows_headless_with_wired_viewmodel()
    {
        EnsureShadTheme();
        using var temp = new TempDirectory("main-window-load");
        var viewModel = new MainViewModel(platformService: new FakePlatformService());
        viewModel.SetModelContext(
            FakeModelPack.Catalog(),
            FakeModelPack.Descriptor().Groups,
            PromptSettingsFactory.CreateDefault(),
            FakeModelPack.Descriptor().DefaultThreshold);
        var doc = new ImageDocument
        {
            Id = "img-1",
            CanonicalPath = temp.WriteFile("img-1.png", [1]),
            FileName = "img-1.png",
            FileSize = 1,
            Format = "png",
            PixelWidth = 4,
            PixelHeight = 4,
            Prediction = new PredictionSnapshot(
                FakeModelPack.Fingerprint().Value,
                DateTimeOffset.UtcNow,
                TimeSpan.FromMilliseconds(5),
                "ORT",
                "CPU",
                1,
                [0.9f, 0.1f, 0.8f, 0.7f, 0.2f, 0.05f]),
        };
        viewModel.AddImported(new Core.Services.ImageImportResult([doc], [], 0));
        viewModel.SyncChildViewModels();
        viewModel.Prompt.Flush();

        var window = new MainWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.NotEmpty(viewModel.Tags.Groups);
        Assert.False(string.IsNullOrEmpty(viewModel.Prompt.PromptText));
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
