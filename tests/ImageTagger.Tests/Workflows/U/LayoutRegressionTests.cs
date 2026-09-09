using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageTagger.App;
using ImageTagger.App.Controls;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Settings;
using ImageTagger.Tests.TestInfrastructure;
using ShadUI;
using Xunit;

namespace ImageTagger.Tests.Workflows.U;

/// <summary>布局回归：分隔条不得自带灰底；开关必须完整落在滚动视口内不被裁切。</summary>
[Trait("Category", TestCategories.Unit)]
public sealed class LayoutRegressionTests
{
    private static bool _appStylesEnsured;

    /// <summary>复刻 App.axaml 全局样式，保证离线验证与生产行为一致。</summary>
    private static void EnsureAppStyles()
    {
        if (Application.Current is null || _appStylesEnsured)
            return;
        _appStylesEnsured = true;

        var cardStyle = new Style(x => x.OfType<Card>());
        cardStyle.Add(new Setter(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled));
        Application.Current.Styles.Add(cardStyle);
    }

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

    private static void EnsureDesignTokens()
    {
        if (Application.Current is null)
            return;
        if (Application.Current.Resources.TryGetResource("PanelSurfaceBrush", null, out _))
            return;

        var tokens = AvaloniaXamlLoader.Load(
            new Uri("avares://ImageTagger.App/Themes/DesignTokens.axaml"));
        if (tokens is ResourceDictionary dictionary)
        {
            foreach (var entry in dictionary)
                Application.Current.Resources[entry.Key] = entry.Value;
        }
    }

    private static Rect ToWindow(Visual visual, TopLevel window)
    {
        var topLeft = visual.TranslatePoint(new Point(0, 0), window) ?? new Point(-1, -1);
        var bottomRight = visual.TranslatePoint(new Point(visual.Bounds.Width, visual.Bounds.Height), window)
            ?? new Point(-1, -1);
        return new Rect(topLeft, bottomRight);
    }

    private static void AssertTogglesFullyVisible(TopLevel window)
    {
        foreach (var toggle in window.GetVisualDescendants().OfType<ToggleSwitch>())
        {
            if (toggle.Bounds.Width < 1)
                continue;
            var rect = ToWindow(toggle, window);
            foreach (var host in toggle.GetVisualAncestors().OfType<ScrollViewer>())
            {
                var viewport = ToWindow(host, window);
                Assert.True(
                    rect.Left >= viewport.Left - 0.5 && rect.Right <= viewport.Right + 0.5,
                    $"toggle {rect} overflows viewport {viewport}");
            }
        }
    }

    private static MainViewModel CreateMainViewModel(TempDirectory temp)
    {
        var viewModel = new MainViewModel();
        viewModel.SetModelContext(
            FakeModelPack.Catalog(),
            FakeModelPack.Descriptor().Groups,
            PromptSettingsFactory.CreateDefault(),
            FakeModelPack.Descriptor().DefaultThreshold);
        viewModel.AddImported(new ImageImportResult(
            [new ImageDocument
            {
                Id = "img-1",
                CanonicalPath = temp.WriteFile("img-1.png", [1]),
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
            }],
            [],
            0));
        viewModel.SyncChildViewModels();
        return viewModel;
    }

    [AvaloniaFact]
    public void Main_window_splitters_are_transparent_and_toggles_are_not_clipped()
    {
        EnsureShadTheme();
        EnsureDesignTokens();
        EnsureAppStyles();
        using var temp = new TempDirectory("layout-main");

        var window = new MainWindow { DataContext = CreateMainViewModel(temp) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Width = 1320;
        window.Height = 830;
        Dispatcher.UIThread.RunJobs();

        foreach (var splitter in window.GetVisualDescendants().OfType<GridSplitter>())
            Assert.Equal(Brushes.Transparent, splitter.Background);

        AssertTogglesFullyVisible(window);

        var toolbarButtons = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("toolbarBtn") && button.Bounds.Height > 0).ToArray();
        Assert.NotEmpty(toolbarButtons);
        Assert.All(toolbarButtons, button => Assert.InRange(button.Bounds.Height, 26, 34));

        // 标签分组不再套卡片。
        var groupCards = window.GetVisualDescendants()
            .OfType<Card>()
            .Where(card => card.DataContext is TagGroupViewModel)
            .ToList();
        Assert.True(groupCards.Count == 0);

        // 标签胶囊为单 TextBlock 多 Run，同基线天然对齐。
        var capsules = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(button => button.Classes.Contains("tagCapsule"))
            .ToList();
        Assert.NotEmpty(capsules);
        foreach (var capsule in capsules)
            Assert.Single(capsule.GetVisualDescendants().OfType<TextBlock>());

        Assert.Equal(new CornerRadius(8), window.RootCornerRadius);

        // 最大化/还原一轮后圆角仍在（主题在状态切换时会重置圆角）。
        window.WindowState = WindowState.Maximized;
        Dispatcher.UIThread.RunJobs();
        window.WindowState = WindowState.Normal;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(new CornerRadius(8), window.RootCornerRadius);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Scroll_gutter_is_only_reserved_while_scrollbar_is_visible()
    {
        EnsureShadTheme();
        EnsureDesignTokens();
        EnsureAppStyles();
        using var temp = new TempDirectory("layout-gutter");

        var window = new MainWindow { DataContext = CreateMainViewModel(temp) };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Width = 1320;
        window.Height = 830;
        Dispatcher.UIThread.RunJobs();

        var right = window.GetVisualDescendants()
            .OfType<ScrollViewer>()
            .First(sv => sv.Classes.Contains("rightPaneScroll"));
        Assert.False(NeedsVerticalBar(right));
        Assert.Equal(new Thickness(1, 1, 1, -2), right.Padding);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Scroll_gutter_appears_only_while_content_overflows()
    {
        EnsureShadTheme();
        EnsureDesignTokens();
        EnsureAppStyles();

        var content = new StackPanel { Spacing = 4 };
        for (var i = 0; i < 20; i++)
            content.Children.Add(new Border { Height = 50 });

        var viewer = new ScrollViewer
        {
            Width = 200,
            Height = 200,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = content,
        };
        var gutter = new MultiBinding
        {
            Converter = ScrollGutterConverter.Instance,
            ConverterParameter = "1",
        };
        gutter.Bindings.Add(new Binding("Extent") { Source = viewer });
        gutter.Bindings.Add(new Binding("Viewport") { Source = viewer });
        viewer.Bind(ScrollViewer.PaddingProperty, gutter);

        var window = new Avalonia.Controls.Window { Width = 200, Height = 200, Content = viewer };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(NeedsVerticalBar(viewer));
        Assert.Equal(12, viewer.Padding.Right);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private static bool NeedsVerticalBar(ScrollViewer viewer) =>
        viewer.Extent.Height > viewer.Viewport.Height + 0.5;

    [AvaloniaFact]
    public void Settings_window_toggles_are_not_clipped()
    {
        EnsureShadTheme();
        EnsureDesignTokens();
        EnsureAppStyles();
        using var temp = new TempDirectory("layout-settings");
        var store = new SettingsStore(temp.CreateSubdirectory("settings"));
        var viewModel = new SettingsViewModel(
            store,
            new EmptyPackService(),
            new FakePlatformService(),
            new FakeAppResourceLocator(temp.CreateSubdirectory("resources")));

        var window = new SettingsWindow { DataContext = viewModel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        AssertTogglesFullyVisible(window);

        Assert.Equal(new CornerRadius(8), window.RootCornerRadius);

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class EmptyPackService : IModelPackService
    {
        public IReadOnlyList<ModelPackInfo> Discover() => Array.Empty<ModelPackInfo>();

        public Task<ModelDescriptor> InstallAsync(string itmodelPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("install not stubbed");

        public Task<LoadedModelPack> LoadAsync(string modelPackId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("load not stubbed");

        public void Uninstall(string modelPackId)
        {
        }
    }
}
