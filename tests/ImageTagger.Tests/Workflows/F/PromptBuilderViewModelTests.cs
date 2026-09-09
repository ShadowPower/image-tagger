using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.F;

/// <summary>PromptBuilderViewModel 测试：防抖、复制、导出与切图隔离（不依赖 UI 线程）。</summary>
[Trait("Category", "Unit")]
public sealed class PromptBuilderViewModelTests
{
    private static readonly GroupDescriptor[] ManifestGroups =
    [
        new GroupDescriptor("rating", "Rating", "分级"),
        new GroupDescriptor("character", "Character", "角色"),
        new GroupDescriptor("general", "General", "通用"),
    ];

    [Fact]
    [Trait("Category", "Unit")]
    public void Flush_rebuilds_synchronously_and_reports_counts()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
            new TagCatalogEntry(1, "cloud", "云", "general"),
        ]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        using var viewModel = new PromptBuilderViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f, 0.8f]), new TagSelection(), 0.5, settings, ManifestGroups);
        viewModel.Flush();

        Assert.Equal("sky, cloud", viewModel.PromptText);
        Assert.Equal(2, viewModel.TagCount);
        Assert.Equal(viewModel.PromptText.Length, viewModel.CharCount);
        Assert.False(viewModel.IsEmpty);
    }

    /// <summary>
    /// 回归：防抖回调在线程池触发时，重算必须 Post 回 SetInputs 时捕获的
    /// 同步上下文，直接在回调线程写属性会跨线程崩溃（真实界面绑定）。
    /// </summary>
    [Fact]
    [Trait("Category", "Unit")]
    public void Debounced_rebuild_posts_to_captured_context()
    {
        var catalog = new TagCatalog([new TagCatalogEntry(0, "sky", "天空", "general")]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var pump = new ManualSyncContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            using var viewModel = new PromptBuilderViewModel();
            viewModel.SetInputs(catalog, Snapshot([0.9f]), new TagSelection(), 0.5, settings, ManifestGroups);

            // 轮询等待：不能 await（续体会 Post 到伪造上下文而饿死）；
            // Thread.Sleep 不触发 xUnit1031，伪造上下文不执行续体故不会死锁。
            WaitFor(() => pump.PostedCount > 0);

            // 回调线程与捕获上下文不同，必须经 Post，中转前属性尚未更新。
            Assert.Equal(1, pump.PostedCount);
            Assert.True(viewModel.IsEmpty);

            pump.PumpAll();

            Assert.Equal("sky", viewModel.PromptText);
            Assert.False(viewModel.IsEmpty);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Debounced_rebuild_runs_inline_without_captured_context()
    {
        var catalog = new TagCatalog([new TagCatalogEntry(0, "sky", "天空", "general")]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var viewModel = new PromptBuilderViewModel();
            viewModel.SetInputs(catalog, Snapshot([0.9f]), new TagSelection(), 0.5, settings, ManifestGroups);

            string? prompt = null;
            WaitFor(() =>
            {
                prompt = viewModel.PromptText;
                return !string.IsNullOrEmpty(prompt);
            });

            Assert.Equal("sky", prompt);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static void WaitFor(Func<bool> condition, int timeoutMilliseconds = 5000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
            Thread.Sleep(20);
    }

    private sealed class ManualSyncContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PostedCount
        {
            get
            {
                lock (_queue)
                    return _queue.Count;
            }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_queue)
                _queue.Enqueue((d, state));
        }

        public void PumpAll()
        {
            while (true)
            {
                (SendOrPostCallback callback, object? state) next;
                lock (_queue)
                {
                    if (_queue.Count == 0)
                        return;
                    next = _queue.Dequeue();
                }
                next.callback(next.state);
            }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Empty_snapshot_reports_empty_and_copy_does_nothing()
    {
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var platform = new FakePlatformService();
        using var viewModel = new PromptBuilderViewModel();
        viewModel.SetInputs(null, null, new TagSelection(), 0.5, settings, ManifestGroups);
        viewModel.Flush();

        Assert.True(viewModel.IsEmpty);
        Assert.Equal(string.Empty, viewModel.PromptText);
        Assert.Equal(0, viewModel.TagCount);

        var copied = await viewModel.CopyAsync(platform, TestContext.Current.CancellationToken);
        Assert.False(copied);
        Assert.Null(platform.ClipboardText);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Copy_async_writes_prompt_to_clipboard()
    {
        var catalog = new TagCatalog([new TagCatalogEntry(0, "sky", "天空", "general")]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        var platform = new FakePlatformService();
        using var viewModel = new PromptBuilderViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f]), new TagSelection(), 0.5, settings, ManifestGroups);
        viewModel.Flush();

        var copied = await viewModel.CopyAsync(platform, TestContext.Current.CancellationToken);

        Assert.True(copied);
        Assert.Equal("sky", platform.ClipboardText);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Export_async_requires_confirmed_path_and_never_overwrites_image()
    {
        using var temp = new TempDirectory("prompt-export");
        var imagePath = temp.WriteFile("photo.png", [1, 2, 3]);
        var catalog = new TagCatalog([new TagCatalogEntry(0, "sky", "天空", "general")]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        using var viewModel = new PromptBuilderViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f]), new TagSelection(), 0.5, settings, ManifestGroups);
        viewModel.Flush();

        // 用户取消：返回 false，不写文件。
        var cancelledPlatform = new FakePlatformService { NextSavePath = null };
        Assert.False(await viewModel.ExportAsync(cancelledPlatform, imagePath, TestContext.Current.CancellationToken));

        // 确认同名 .txt：写入 Prompt。
        var targetPath = Path.Combine(temp.FullPath, "photo.txt");
        var platform = new FakePlatformService { NextSavePath = targetPath };
        Assert.True(await viewModel.ExportAsync(platform, imagePath, TestContext.Current.CancellationToken));
        Assert.Equal("sky", await File.ReadAllTextAsync(targetPath, TestContext.Current.CancellationToken));

        // 若确认路径恰为图片本体：拒绝覆盖。
        var overwritePlatform = new FakePlatformService { NextSavePath = imagePath };
        Assert.False(await viewModel.ExportAsync(overwritePlatform, imagePath, TestContext.Current.CancellationToken));
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(imagePath, TestContext.Current.CancellationToken));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Switching_image_does_not_reuse_previous_prompt()
    {
        var catalog = new TagCatalog(
        [
            new TagCatalogEntry(0, "sky", "天空", "general"),
            new TagCatalogEntry(1, "sea", "大海", "general"),
        ]);
        var settings = PromptSettingsFactory.CreateDefault(ManifestGroups);
        using var viewModel = new PromptBuilderViewModel();
        viewModel.SetInputs(catalog, Snapshot([0.9f, 0.1f]), new TagSelection(), 0.5, settings, ManifestGroups);
        viewModel.Flush();
        Assert.Equal("sky", viewModel.PromptText);

        // 立即切换来源：即使防抖未触发，Flush 后也必须为新图结果。
        viewModel.SetInputs(catalog, Snapshot([0.1f, 0.9f]), new TagSelection(), 0.5, settings, ManifestGroups);
        viewModel.Flush();
        Assert.Equal("sea", viewModel.PromptText);
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
