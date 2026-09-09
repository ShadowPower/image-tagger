using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.Styling;
using ImageTagger.App;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Settings;
using ShadUI;
using Xunit;
using ImageTagger.Tests.TestInfrastructure;

namespace ImageTagger.Tests.VisualReview;

/// <summary>
/// Explicit, human-oriented screenshot harness. It is skipped by default and
/// renders only when IMAGETAGGER_RENDER_UI=1 is set.
/// </summary>
public sealed class UiScreenshotTests
{
    [AvaloniaFact]
    public void Render_requested_ui_state_to_png()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("IMAGETAGGER_RENDER_UI"), "1", StringComparison.Ordinal))
            Assert.Skip("Set IMAGETAGGER_RENDER_UI=1 to render a visual-review screenshot.");

        EnsureTheme();
        var options = ScreenshotOptions.Read();
        if (Application.Current is not null)
            Application.Current.RequestedThemeVariant = options.Theme == "dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        using var temp = new TempDirectory("visual-review");
        var viewModel = CreateMainViewModel(temp, options.State);
        Avalonia.Controls.Window window = options.Window == "settings"
            ? new SettingsWindow { DataContext = CreateSettingsViewModel(temp) }
            : new MainWindow { DataContext = viewModel };
        window.Width = options.Width;
        window.Height = options.Height;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        if (window is SettingsWindow settingsWindow)
        {
            var navigation = settingsWindow.FindControl<ListBox>("SectionNav");
            if (navigation is not null)
                navigation.SelectedIndex = options.State switch { "appearance" => 1, "performance" => 2, _ => 0 };
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.GetLastRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal(new PixelSize(options.Width, options.Height), frame!.PixelSize);
        var output = options.OutputPath ?? Path.Combine(Path.GetTempPath(),
            $"imagetagger-{options.Window}-{options.State}-{options.Width}x{options.Height}.png");
        frame.Save(output, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Assert.True(new FileInfo(output).Length > 1024, $"Screenshot was not written: {output}");
        window.Close();
    }

    private static MainViewModel CreateMainViewModel(TempDirectory temp, string state)
    {
        var vm = new MainViewModel();
        if (state == "empty") return vm;
        var document = new ImageDocument
        {
            Id = "visual-review-image",
            CanonicalPath = temp.WriteFile("visual-review.png", [1]),
            FileName = "visual-review.png",
            FileSize = 1,
            Format = "png",
            PixelWidth = 800,
            PixelHeight = 600,
        };
        if (state == "recognized")
        {
            document.Prediction = new PredictionSnapshot(FakeModelPack.Fingerprint().Value,
                DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(12), "ORT", "CPU", 1,
                [0.95f, 0.82f, 0.72f, 0.31f, 0.18f, 0.08f]);
            document.AnalysisState = AnalysisState.Succeeded;
        }
        else if (state == "error")
        {
            document.AnalysisState = AnalysisState.Failed;
            document.LastError = "示例错误：模型推理失败";
        }
        vm.SetModelContext(FakeModelPack.Catalog(), FakeModelPack.Descriptor().Groups,
            PromptSettingsFactory.CreateDefault(), FakeModelPack.Descriptor().DefaultThreshold);
        vm.AddImported(new ImageImportResult([document], [], 0));
        if (state == "busy") vm.BeginBatch(1);
        return vm;
    }

    private static SettingsViewModel CreateSettingsViewModel(TempDirectory temp) =>
        new(new MemorySettingsStore(), new StubModelService(), new StubPlatform(), new StubLocator(temp.FullPath));

    private static void EnsureTheme()
    {
        if (Application.Current is null) return;
        Application.Current.Styles.Add(new ShadTheme());
        var resources = AvaloniaXamlLoader.Load(new Uri("avares://ImageTagger.App/Themes/DesignTokens.axaml"));
        if (resources is ResourceDictionary dictionary)
            foreach (var entry in dictionary) Application.Current.Resources[entry.Key] = entry.Value;
    }

    private sealed record ScreenshotOptions(string Window, string State, string Theme, int Width, int Height, string? OutputPath)
    {
        public static ScreenshotOptions Read() => new(
            Env("IMAGETAGGER_SCREENSHOT_WINDOW", "main"),
            Env("IMAGETAGGER_SCREENSHOT_STATE", "recognized"),
            Env("IMAGETAGGER_SCREENSHOT_THEME", "light"),
            IntEnv("IMAGETAGGER_SCREENSHOT_WIDTH", 1080),
            IntEnv("IMAGETAGGER_SCREENSHOT_HEIGHT", 680),
            Environment.GetEnvironmentVariable("IMAGETAGGER_SCREENSHOT_OUTPUT"));

        private static string Env(string name, string fallback) =>
            Environment.GetEnvironmentVariable(name)?.Trim().ToLowerInvariant() is { Length: > 0 } value ? value : fallback;
        private static int IntEnv(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }

    private sealed class MemorySettingsStore : ISettingsStore
    {
        private AppSettings _settings = new();
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings) => _settings = settings;
    }

    private sealed class StubModelService : IModelPackService
    {
        public Task<LoadedModelPack> LoadFromPathAsync(string path, CancellationToken token) => throw new NotSupportedException();
        public ModelDescriptor ReadDescriptorFromPath(string path) => throw new NotSupportedException();
    }

    private sealed class StubLocator(string root) : IAppResourceLocator
    {
        public string SettingsRoot => Path.Combine(root, "settings");
        public string LogsRoot => Path.Combine(root, "logs");
        public string CacheRoot => Path.Combine(root, "cache");
    }

    private sealed class StubPlatform : IPlatformService
    {
        public Task<IReadOnlyList<string>?> PickImageFilesAsync(CancellationToken t) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<string?> PickFolderAsync(CancellationToken t) => Task.FromResult<string?>(null);
        public Task<string?> PickSaveFileAsync(string n, CancellationToken t) => Task.FromResult<string?>(null);
        public Task SetClipboardTextAsync(string t, CancellationToken c) => Task.CompletedTask;
        public Task<bool> RevealInFileManagerAsync(string p, CancellationToken c) => Task.FromResult(false);
        public Task<bool> OpenFileAsync(string p, CancellationToken c) => Task.FromResult(false);
    }
}
