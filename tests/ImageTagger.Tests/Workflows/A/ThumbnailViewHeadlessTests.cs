using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ImageTagger.App.Controls;
using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.A;

public sealed class ThumbnailViewHeadlessTests
{
    [AvaloniaFact]
    public async Task Thumbnail_is_requested_only_when_container_is_realized()
    {
        var service = new CountingThumbnailService();
        var item = new ImageListItemViewModel(Document(), service);
        var view = new ThumbnailView { Width = 64, Height = 64, Item = item };
        var window = new Window { Width = 100, Height = 100, Content = view };

        Assert.Equal(0, service.CallCount);
        window.Show();
        await Determinism.PumpUntilAsync(
            () => item.Thumbnail is not null,
            TimeSpan.FromSeconds(5),
            "realized thumbnail loads");

        Assert.Equal(1, service.CallCount);
        Assert.Equal(64, view.Bounds.Width);
        Assert.Equal(64, view.Bounds.Height);
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Thousand_item_list_realizes_only_visible_thumbnails()
    {
        var service = new CountingThumbnailService();
        var items = Enumerable.Range(0, 1_000)
            .Select(index => new ImageListItemViewModel(Document($"item-{index}"), service))
            .ToArray();
        var list = new ListBox
        {
            ItemsSource = items,
            ItemTemplate = new FuncDataTemplate<ImageListItemViewModel>((item, _) =>
                new ThumbnailView { Width = 64, Height = 64, Item = item }),
        };
        var window = new Window { Width = 300, Height = 320, Content = list };

        window.Show();
        window.ApplyTemplate();
        list.ApplyTemplate();
        list.Measure(new Size(300, 320));
        list.Arrange(new Rect(0, 0, 300, 320));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.InRange(service.CallCount, 0, 50);
        list.ScrollIntoView(999);
        list.Measure(new Size(300, 320));
        list.Arrange(new Rect(0, 0, 300, 320));
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Assert.InRange(service.CallCount, 0, 100);
        window.Close();
        foreach (var item in items)
            item.Dispose();
    }

    private static ImageDocument Document(string id = "thumbnail-view") => new()
    {
        Id = id,
        CanonicalPath = Path.GetFullPath($"{id}.png"),
        FileName = $"{id}.png",
        FileSize = 4,
        Format = "png",
        PixelWidth = 1,
        PixelHeight = 1,
    };

    private sealed class CountingThumbnailService : IThumbnailService
    {
        public int CallCount { get; private set; }

        public Task<ThumbnailResult?> GetOrCreateAsync(ImageDocument image, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<ThumbnailResult?>(new ThumbnailResult(1, 1, [30, 20, 10, 128]));
        }
    }
}
