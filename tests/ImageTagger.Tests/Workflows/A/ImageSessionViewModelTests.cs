using ImageTagger.App.ViewModels;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.Services;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.A;

public sealed class ImageSessionViewModelTests
{
    [Fact]
    public void Selection_navigation_remove_and_clear_are_identity_based_and_never_delete_files()
    {
        using var temp = new TempDirectory("session");
        var paths = Enumerable.Range(1, 3)
            .Select(index => temp.WriteFile($"image-{index}.png", [1, 2, 3]))
            .ToArray();
        var documents = paths.Select(Document).ToArray();
        var viewModel = new MainViewModel();

        viewModel.AddImported(new ImageImportResult(documents, [], 0));

        Assert.Equal(documents[0].Id, viewModel.CurrentImageId);
        Assert.False(viewModel.SelectPreviousCommand.CanExecute(null));
        Assert.True(viewModel.SelectNextCommand.CanExecute(null));
        viewModel.SelectNextCommand.Execute(null);
        Assert.Equal(documents[1].Id, viewModel.CurrentImageId);
        Assert.True(viewModel.SelectById(documents[2].Id));
        Assert.Same(documents[2], viewModel.SelectedImage?.Document);

        viewModel.RemoveSelectedCommand.Execute(null);
        Assert.Equal(documents[1].Id, viewModel.CurrentImageId);
        Assert.Equal(2, viewModel.Images.Count);
        Assert.All(paths, path => Assert.True(File.Exists(path)));

        viewModel.ClearImagesCommand.Execute(null);
        Assert.Empty(viewModel.Images);
        Assert.Null(viewModel.CurrentImageId);
        Assert.Equal(WindowPhase.Empty, viewModel.Phase);
        Assert.All(paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public async Task Late_thumbnail_result_stays_on_its_image_and_clear_cancels_commit()
    {
        var thumbnail = new DeferredThumbnailService();
        var first = Document("first.png");
        var second = Document("second.png");
        var viewModel = new MainViewModel(thumbnailService: thumbnail);
        viewModel.AddImported(new ImageImportResult([first, second], [], 0));
        var firstItem = viewModel.Images[0];

        var load = firstItem.EnsureThumbnailAsync(TestContext.Current.CancellationToken);
        viewModel.SelectById(second.Id);
        thumbnail.Complete(new ThumbnailResult(1, 1, [3, 2, 1, 255]));
        await load;

        Assert.NotNull(firstItem.Thumbnail);
        Assert.Null(viewModel.SelectedImage?.Thumbnail);

        thumbnail.Reset();
        var lateLoad = viewModel.SelectedImage!.EnsureThumbnailAsync(TestContext.Current.CancellationToken);
        viewModel.ClearImagesCommand.Execute(null);
        thumbnail.Complete(new ThumbnailResult(1, 1, [6, 5, 4, 255]));
        await lateLoad;
        Assert.Empty(viewModel.Images);
        Assert.Null(viewModel.CurrentImageId);
    }

    [Fact]
    public void Item_exposes_all_compact_list_information()
    {
        var document = WithState();
        var item = new ImageListItemViewModel(document, null);

        Assert.Equal("photo.jpeg", item.FileName);
        Assert.Equal("JPEG", item.Format);
        Assert.Equal("640×480", item.DimensionsText);
        Assert.Equal("1.5 KiB", item.FileSizeText);
        Assert.Equal(AnalysisState.Succeeded, item.AnalysisState);
        item.RefreshAnalysisState(0.55);
        Assert.Equal(1, item.VisibleTagCount);
        Assert.Equal("已识别 1 个标签", item.StatusText);

        static ImageDocument WithState() => new()
        {
            Id = "with-state",
            CanonicalPath = Path.GetFullPath("photo.jpeg"),
            FileName = "photo.jpeg",
            FileSize = 1536,
            Format = "jpeg",
            PixelWidth = 640,
            PixelHeight = 480,
            AnalysisState = AnalysisState.Succeeded,
            Prediction = new PredictionSnapshot(
                "model", DateTimeOffset.UnixEpoch, TimeSpan.Zero, "runtime", "cpu", 1, [0.5f, 0.6f]),
        };
    }

    [Fact]
    public async Task Toolbar_commands_import_raise_recognition_and_use_platform_actions()
    {
        var document = Document("picked.png");
        var platform = new FakePlatformService { NextPickedFiles = [document.CanonicalPath] };
        var viewModel = new MainViewModel(
            imageImportService: new FakeImageImportService(document),
            platformService: platform);
        RecognitionRequestedEventArgs? request = null;
        viewModel.RecognitionRequested += (_, args) => request = args;

        Assert.True(viewModel.OpenImagesCommand.CanExecute(null));
        Assert.False(viewModel.RecognizeCurrentCommand.CanExecute(null));
        await viewModel.OpenImagesCommand.ExecuteAsync(null);

        Assert.Single(viewModel.Images);
        Assert.Equal("picked.png", viewModel.CurrentFileName);
        Assert.Contains("1×1", viewModel.CurrentFileDetails, StringComparison.Ordinal);
        Assert.True(viewModel.RecognizeCurrentCommand.CanExecute(null));
        viewModel.RecognizeCurrentCommand.Execute(null);
        Assert.False(request?.RecognizeAll);
        Assert.Equal(document.Id, request?.ImageId);

        await viewModel.CopyPathCommand.ExecuteAsync(viewModel.SelectedImage);
        await viewModel.RevealItemCommand.ExecuteAsync(viewModel.SelectedImage);
        await viewModel.OpenItemCommand.ExecuteAsync(viewModel.SelectedImage);
        Assert.Equal(document.CanonicalPath, platform.ClipboardText);
        Assert.Equal(document.CanonicalPath, platform.LastRevealedPath);
        Assert.Equal(document.CanonicalPath, platform.LastOpenedPath);

        var originalPaneState = viewModel.IsRightPaneExpanded;
        viewModel.ToggleRightPaneCommand.Execute(null);
        Assert.NotEqual(originalPaneState, viewModel.IsRightPaneExpanded);
    }

    [Fact]
    public void Busy_and_batch_state_update_command_availability_and_progress()
    {
        var document = Document("batch.png");
        var platform = new FakePlatformService();
        var viewModel = new MainViewModel(
            imageImportService: new FakeImageImportService(document),
            platformService: platform);
        viewModel.AddImported(new ImageImportResult([document], [], 0));

        viewModel.BeginBatch(4);

        Assert.True(viewModel.IsOperationRunning);
        Assert.Equal(WindowPhase.Busy, viewModel.Phase);
        Assert.False(viewModel.OpenImagesCommand.CanExecute(null));
        Assert.False(viewModel.RecognizeCurrentCommand.CanExecute(null));
        Assert.False(viewModel.RemoveSelectedCommand.CanExecute(null));
        Assert.True(viewModel.CancelCommand.CanExecute(null));
        viewModel.ReportBatchProgress(3);
        Assert.Equal("3/4", viewModel.BatchProgressText);

        viewModel.EndBatch();
        Assert.False(viewModel.IsOperationRunning);
        Assert.Equal(WindowPhase.Succeeded, viewModel.Phase);
        Assert.True(viewModel.RecognizeCurrentCommand.CanExecute(null));
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public async Task Picker_cancel_and_external_open_failure_are_non_blocking()
    {
        var platform = new FakePlatformService
        {
            NextPickedFiles = null,
            ExternalLaunchSucceeds = false,
        };
        var document = Document("cannot-open.png");
        var viewModel = new MainViewModel(
            imageImportService: new FakeImageImportService(document),
            platformService: platform);

        await viewModel.OpenImagesCommand.ExecuteAsync(null);
        Assert.Empty(viewModel.Images);
        Assert.Equal("就绪", viewModel.StatusBarText);

        viewModel.AddImported(new ImageImportResult([document], [], 0));
        await viewModel.OpenItemCommand.ExecuteAsync(viewModel.SelectedImage);
        Assert.Equal("无法使用默认应用打开该文件", viewModel.StatusBarText);
        await viewModel.RevealItemCommand.ExecuteAsync(viewModel.SelectedImage);
        Assert.Equal("无法在文件管理器中显示该文件", viewModel.StatusBarText);
    }

    private static ImageDocument Document(string path) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CanonicalPath = Path.GetFullPath(path),
        FileName = Path.GetFileName(path),
        FileSize = 3,
        Format = "png",
        PixelWidth = 1,
        PixelHeight = 1,
    };

    private sealed class DeferredThumbnailService : IThumbnailService
    {
        private TaskCompletionSource<ThumbnailResult?> _completion = NewCompletion();

        public Task<ThumbnailResult?> GetOrCreateAsync(ImageDocument image, CancellationToken cancellationToken) =>
            _completion.Task;

        public void Complete(ThumbnailResult result) => _completion.TrySetResult(result);

        public void Reset() => _completion = NewCompletion();

        private static TaskCompletionSource<ThumbnailResult?> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
