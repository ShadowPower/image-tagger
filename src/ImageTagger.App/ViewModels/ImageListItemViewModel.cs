using CommunityToolkit.Mvvm.ComponentModel;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;

namespace ImageTagger.App.ViewModels;

/// <summary>List presentation for one immutable image identity and its lazy thumbnail.</summary>
public sealed partial class ImageListItemViewModel : ObservableObject, IDisposable
{
    private readonly IThumbnailService? _thumbnailService;
    private CancellationTokenSource? _thumbnailCancellation;

    public ImageListItemViewModel(ImageDocument document, IThumbnailService? thumbnailService)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _thumbnailService = thumbnailService;
    }

    public ImageDocument Document { get; }

    public string Id => Document.Id;
    public string CanonicalPath => Document.CanonicalPath;
    public string FileName => Document.FileName;
    public string Format => Document.Format.ToUpperInvariant();
    public int PixelWidth => Document.PixelWidth;
    public int PixelHeight => Document.PixelHeight;
    public string DimensionsText => $"{PixelWidth}×{PixelHeight}";
    public string FileSizeText => FormatFileSize(Document.FileSize);
    public AnalysisState AnalysisState => Document.AnalysisState;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private int _visibleTagCount;

    public string StatusText => Document.AnalysisState switch
    {
        AnalysisState.NotRun => "未识别",
        AnalysisState.Queued => "排队中",
        AnalysisState.Running => "识别中",
        AnalysisState.Succeeded => $"已识别 {VisibleTagCount} 个标签",
        AnalysisState.Failed => "识别失败",
        AnalysisState.Canceled => "已取消",
        AnalysisState.Stale => "模型已变更",
        _ => "未知状态",
    };

    [ObservableProperty]
    private ThumbnailResult? _thumbnail;

    public async Task EnsureThumbnailAsync(CancellationToken cancellationToken = default)
    {
        if (Thumbnail is not null || _thumbnailService is null)
            return;

        CancelThumbnailRequest();
        _thumbnailCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var localCancellation = _thumbnailCancellation;
        Document.ThumbnailState = ThumbnailState.Queued;
        try
        {
            var result = await _thumbnailService
                .GetOrCreateAsync(Document, localCancellation.Token)
                .ConfigureAwait(true);
            if (!localCancellation.IsCancellationRequested)
            {
                Thumbnail = result;
                Document.ThumbnailState = result is null ? ThumbnailState.Failed : ThumbnailState.Loaded;
            }
        }
        catch (OperationCanceledException) when (localCancellation.IsCancellationRequested)
        {
            Document.ThumbnailState = ThumbnailState.NotStarted;
        }
        catch (Exception)
        {
            // Thumbnail loading is best-effort; never let an async-void UI
            // lifecycle callback surface an unhandled exception.
            Document.ThumbnailState = ThumbnailState.Failed;
        }
        finally
        {
            if (ReferenceEquals(_thumbnailCancellation, localCancellation))
                _thumbnailCancellation = null;
            localCancellation.Dispose();
        }
    }

    public void CancelThumbnailRequest()
    {
        _thumbnailCancellation?.Cancel();
    }

    /// <summary>Refreshes derived list text after an inference state is atomically replaced.</summary>
    public void RefreshAnalysisState(double threshold)
    {
        if (threshold is < 0 or > 1 || !double.IsFinite(threshold))
            throw new ArgumentOutOfRangeException(nameof(threshold));
        VisibleTagCount = Document.Prediction?.Probabilities.Count(value => value >= threshold) ?? 0;
        OnPropertyChanged(nameof(AnalysisState));
        OnPropertyChanged(nameof(StatusText));
    }

    public void Dispose()
    {
        CancelThumbnailRequest();
        _thumbnailCancellation?.Dispose();
        _thumbnailCancellation = null;
    }

    private static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
