using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Metadata;

namespace ImageTagger.App.ViewModels;

/// <summary>生成信息参数网格的一行：规范化参数名与纯文本值（只读展示，可复制）。</summary>
public sealed record ParameterRow(string Key, string Value);

/// <summary>
/// Generation-info tab state with lazy per-image parsing and a session cache.
/// Bulk import never pre-parses; the first time an image becomes current its
/// metadata is parsed asynchronously and cached by image id. Late results are
/// written back to their originating image only: display updates apply solely
/// when the completed request still matches the current image id, so switching
/// images discards stale UI updates without losing the cached value.
/// All text stays plain strings; the view model never parses HTML.
/// </summary>
public sealed partial class MetadataViewModel : ObservableObject
{
    private readonly IGenerationInfoParser _parser;
    private readonly Dictionary<string, GenerationInfo?> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<GenerationInfo?>> _inflight = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private string? _currentImageId;
    private long _requestVersion;

    public MetadataViewModel(IGenerationInfoParser? parser = null)
    {
        _parser = parser ?? new GenerationInfoParser();
    }

    [ObservableProperty]
    private string _sourceBadge = "未解析";

    [ObservableProperty]
    private bool _isParsed;

    [ObservableProperty]
    private bool _isParsing;

    [ObservableProperty]
    private string _positive = string.Empty;

    [ObservableProperty]
    private string _negative = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ParameterRows))]
    private IReadOnlyDictionary<string, string> _parameters =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>参数网格行投影（诊断与 XAML 绑定用，由 <see cref="Parameters"/> 派生）。</summary>
    public IReadOnlyList<ParameterRow> ParameterRows => Parameters
        .OrderBy(entry => entry.Key, StringComparer.Ordinal)
        .Select(entry => new ParameterRow(entry.Key, entry.Value))
        .ToArray();

    [ObservableProperty]
    private IReadOnlyList<GenerationResource> _resources = [];

    [ObservableProperty]
    private string _rawText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPartialWarning))]
    private IReadOnlyList<string> _warnings = [];

    public bool HasPartialWarning => Warnings.Count > 0;

    /// <summary>Number of cached images, exposed for diagnostics and tests.</summary>
    public int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _cache.Count;
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="image"/> current, parsing lazily on cache miss.
    /// Callers may invoke this on every selection change; unparsable or missing
    /// metadata resolves to a parsed-but-empty state rather than an exception.
    /// </summary>
    public async Task SetCurrentAsync(ImageDocument? image, CancellationToken cancellationToken = default)
    {
        long version;
        lock (_gate)
        {
            _currentImageId = image?.Id;
            version = ++_requestVersion;
        }

        if (image is null)
        {
            ApplyEmpty();
            return;
        }

        var imageId = image.Id;
        lock (_gate)
        {
            if (_cache.TryGetValue(imageId, out var cached) || _cache.ContainsKey(imageId))
            {
                if (IsCurrent(version, imageId))
                {
                    ApplyInfo(cached);
                }

                return;
            }
        }

        Task<GenerationInfo?> task;
        lock (_gate)
        {
            if (!_inflight.TryGetValue(imageId, out task!))
            {
                task = ParseAndCacheAsync(image, imageId, cancellationToken);
                _inflight[imageId] = task;
            }
        }

        if (IsCurrent(version, imageId))
        {
            ApplyLoading();
        }

        GenerationInfo? result;
        try
        {
            result = await task.ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            result = null;
            lock (_gate)
            {
                _cache.TryAdd(imageId, null);
            }
        }
        finally
        {
            lock (_gate)
            {
                _inflight.Remove(imageId);
            }
        }

        if (IsCurrent(version, imageId))
        {
            ApplyInfo(result);
        }
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    private async Task<GenerationInfo?> ParseAndCacheAsync(
        ImageDocument image, string imageId, CancellationToken cancellationToken)
    {
        GenerationInfo? info;
        try
        {
            info = await _parser.ParseAsync(image, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            info = null;
        }

        lock (_gate)
        {
            _cache[imageId] = info;
        }

        // Write back to the originating document only, never to the current one.
        image.Generation = info;
        return info;
    }

    private bool IsCurrent(long version, string imageId)
    {
        lock (_gate)
        {
            return version == _requestVersion && string.Equals(_currentImageId, imageId, StringComparison.Ordinal);
        }
    }

    private void ApplyEmpty()
    {
        IsParsing = false;
        IsParsed = false;
        SourceBadge = "未解析";
        Positive = string.Empty;
        Negative = string.Empty;
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        Resources = [];
        RawText = string.Empty;
        Warnings = [];
    }

    private void ApplyLoading()
    {
        IsParsing = true;
        IsParsed = false;
        SourceBadge = "解析中…";
        Positive = string.Empty;
        Negative = string.Empty;
        Parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        Resources = [];
        RawText = string.Empty;
        Warnings = [];
    }

    private void ApplyInfo(GenerationInfo? info)
    {
        IsParsing = false;
        IsParsed = info is not null;
        SourceBadge = info?.Source switch
        {
            GenerationSource.Automatic1111 => "AUTOMATIC1111",
            GenerationSource.Forge => "Forge",
            GenerationSource.ComfyUi => "ComfyUI",
            GenerationSource.NovelAi => "NovelAI",
            _ => "未发现生成信息",
        };
        Positive = info?.PositivePrompt ?? string.Empty;
        Negative = info?.NegativePrompt ?? string.Empty;
        Parameters = info?.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
        Resources = info?.Resources ?? [];
        RawText = BuildRawText(info);
        Warnings = info?.Warnings ?? [];
    }

    private static string BuildRawText(GenerationInfo? info)
    {
        if (info?.RawEntries is null || info.RawEntries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        foreach (var entry in info.RawEntries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(entry.Key).Append(": ").Append(entry.Value).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }
}
