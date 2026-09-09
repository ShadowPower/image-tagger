using CommunityToolkit.Mvvm.ComponentModel;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;

namespace ImageTagger.App.ViewModels;

/// <summary>
/// Prompt 构建器 ViewModel：监听当前图片结果、选择、阈值、规则四输入，
/// 100ms 防抖重算，输出 Prompt 文本、标签数、字符数与空状态。
/// 切换图片立即切换来源，不沿用上一张。测试可用 Flush() 同步触发。
/// </summary>
public sealed partial class PromptBuilderViewModel : ObservableObject, IDisposable
{
    private const int DebounceMilliseconds = 100;

    private readonly object _gate = new();
    private TagCatalog? _catalog;
    private PredictionSnapshot? _prediction;
    private TagSelection? _selection;
    private double _defaultThreshold;
    private PromptSettings? _settings;
    private IReadOnlyList<GroupDescriptor>? _manifestGroups;
    private Timer? _debounceTimer;
    private SynchronizationContext? _syncContext;
    private bool _disposed;

    public PromptBuilderViewModel()
    {
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string _promptText = string.Empty;

    [ObservableProperty]
    private int _tagCount;

    [ObservableProperty]
    private int _charCount;

    public bool IsEmpty => string.IsNullOrEmpty(PromptText);

    /// <summary>设置四类输入并启动 100ms 防抖重算（最新来源立即替换旧来源）。</summary>
    public void SetInputs(
        TagCatalog? catalog,
        PredictionSnapshot? prediction,
        TagSelection? selection,
        double defaultThreshold,
        PromptSettings? settings,
        IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _catalog = catalog;
            _prediction = prediction;
            _selection = selection;
            _defaultThreshold = defaultThreshold;
            _settings = settings;
            _manifestGroups = manifestGroups;
            // 调用方（UI 线程）当前的同步上下文；防抖回调在线程池触发，
            // 重算必须 Post 回该上下文，否则跨线程更新绑定会崩溃。
            _syncContext = SynchronizationContext.Current;
            _debounceTimer ??= new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
        }
    }

    /// <summary>取消防抖并同步重算一次，供测试与需要立即刷新的场景使用。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _debounceTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        Rebuild();
    }

    /// <summary>复制当前 Prompt；空 Prompt 不复制，返回 false。</summary>
    public async Task<bool> CopyAsync(IPlatformService platformService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platformService);
        string text = PromptText;
        if (string.IsNullOrEmpty(text))
            return false;
        await platformService.SetClipboardTextAsync(text, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 导出同名 .txt：默认同名 .txt，需用户确认路径，不覆盖图片。
    /// 用户取消或空 Prompt 返回 false。
    /// </summary>
    public async Task<bool> ExportAsync(
        IPlatformService platformService,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(platformService);
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        string text = PromptText;
        if (string.IsNullOrEmpty(text))
            return false;

        string suggestedFileName = Path.GetFileNameWithoutExtension(imagePath) + ".txt";
        if (string.IsNullOrEmpty(suggestedFileName) || suggestedFileName == ".txt")
            suggestedFileName = "prompt.txt";

        string? pickedPath = await platformService
            .PickSaveFileAsync(suggestedFileName, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(pickedPath))
            return false;
        // 不覆盖图片本体。
        if (string.Equals(pickedPath, imagePath, StringComparison.OrdinalIgnoreCase))
            return false;

        var directory = Path.GetDirectoryName(pickedPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(pickedPath, text, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        SynchronizationContext? syncContext;
        lock (_gate)
        {
            if (_disposed)
                return;
            syncContext = _syncContext;
        }
        if (syncContext is not null && !ReferenceEquals(syncContext, SynchronizationContext.Current))
        {
            try
            {
                syncContext.Post(static state => ((PromptBuilderViewModel)state!).Rebuild(), this);
            }
            catch (Exception)
            {
                // 界面已销毁等竞态下本次刷新可丢弃；Flush() 仍可同步重算。
            }
            return;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        TagCatalog? catalog;
        PredictionSnapshot? prediction;
        TagSelection? selection;
        double threshold;
        PromptSettings? settings;
        IReadOnlyList<GroupDescriptor>? groups;
        lock (_gate)
        {
            catalog = _catalog;
            prediction = _prediction;
            selection = _selection;
            threshold = _defaultThreshold;
            settings = _settings;
            groups = _manifestGroups;
        }

        BuildResult result;
        try
        {
            // 空输入返回空结果；参数错误亦视为空，避免后台线程抛异常。
            result = PromptBuilder.Build(catalog, prediction, selection, threshold, settings, groups);
        }
        catch (ArgumentException)
        {
            result = BuildResult.Empty;
        }

        PromptText = result.Prompt;
        TagCount = result.TagCount;
        CharCount = result.CharCount;
    }
}
