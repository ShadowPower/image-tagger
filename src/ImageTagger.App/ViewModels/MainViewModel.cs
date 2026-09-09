using Avalonia;
using Avalonia.Controls;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.Services;
using ImageTagger.App.Services;
using ShadUI;

namespace ImageTagger.App.ViewModels;

/// <summary>UI presentation phase of the center workspace.</summary>
public enum WindowPhase
{
    Empty,
    Succeeded,
    Busy,
    Error,
}

/// <summary>
/// Design-time ready main window view model. Real services are wired in U-01;
/// design mode shows a three-image session in the Succeeded phase.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ThemeWatcher? _themeWatcher;
    private readonly IThumbnailService? _thumbnailService;
    private readonly IImageImportService? _imageImportService;
    private readonly IPlatformService? _platformService;
    private readonly IPromptSettingsStore? _promptStore;
    private readonly IUserNotificationService? _notifications;
    private CancellationTokenSource? _activeOperationCancellation;
    private bool _disposed;

    /// <summary>ShadUI 对话框与 Toast 宿主的管理器；主窗口 Hosts 直接绑定这两个实例。</summary>
    public DialogManager DialogManager { get; } = new();

    /// <summary>ShadUI Toast 管理器；通知服务经 <c>BindShadManagers</c> 接入后可弹出 Toast。</summary>
    public ToastManager ToastManager { get; } = new();

    public TagsViewModel Tags { get; }

    public MetadataViewModel Metadata { get; }

    public PromptBuilderViewModel Prompt { get; }

    /// <summary>当前图片的标签选择（与 Tags/Prompt 共享引用，切图时重建）。</summary>
    public TagSelection CurrentSelection { get; private set; } = new();

    /// <summary>当前模型的共享标签目录；由模型加载流程设置（U-01 接线）。</summary>
    public TagCatalog? CurrentCatalog { get; private set; }

    /// <summary>当前模型的分组声明；默认空表示回落到目录分组。</summary>
    public IReadOnlyList<Core.ModelPacks.GroupDescriptor> CurrentGroups { get; private set; } = [];

    /// <summary>单套 Prompt 规则（默认由 PromptSettingsFactory 创建；规则编辑后由 <see cref="Rules"/> 同步）。</summary>
    public PromptSettings CurrentPromptSettings { get; private set; } =
        Core.Prompt.PromptSettingsFactory.CreateDefault();

    /// <summary>右侧规则编辑器；模型切换时按新分组重建，行顺序即 Prompt 分组顺序。</summary>
    public PromptRulesViewModel Rules { get; private set; }

    public event EventHandler? SettingsRequested;

    public MainViewModel(
        ThemeWatcher? themeWatcher = null,
        IThumbnailService? thumbnailService = null,
        IImageImportService? imageImportService = null,
        IPlatformService? platformService = null,
        TagsViewModel? tags = null,
        MetadataViewModel? metadata = null,
        PromptBuilderViewModel? prompt = null,
        IPromptSettingsStore? promptStore = null,
        PromptRulesViewModel? rules = null,
        IUserNotificationService? notifications = null)
    {
        _themeWatcher = themeWatcher;
        _thumbnailService = thumbnailService;
        _imageImportService = imageImportService;
        _platformService = platformService;
        _promptStore = promptStore;
        _notifications = notifications;
        Tags = tags ?? new TagsViewModel();
        Metadata = metadata ?? new MetadataViewModel();
        Prompt = prompt ?? new PromptBuilderViewModel();
        Rules = rules ?? new PromptRulesViewModel(CurrentPromptSettings, CurrentGroups, promptStore);
        AttachRules(Rules);
        Prompt.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PromptBuilderViewModel.PromptText) or nameof(PromptBuilderViewModel.IsEmpty))
            {
                CopyPromptCommand.NotifyCanExecuteChanged();
                ExportPromptCommand.NotifyCanExecuteChanged();
            }
        };
        Metadata.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MetadataViewModel.IsParsed))
            {
                CopyPositivePromptCommand.NotifyCanExecuteChanged();
                CopyNegativePromptCommand.NotifyCanExecuteChanged();
            }
        };
        Images = [];
        Images.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ImageCount));
            RefreshFilteredImages();
            NotifySessionCommandStates();
        };

        if (Design.IsDesignMode)
        {
            foreach (var image in DesignData.Session())
            {
                var item = new ImageListItemViewModel(image, thumbnailService);
                item.RefreshAnalysisState(ThresholdPercent / 100);
                Images.Add(item);
            }
            SelectedImage = Images.FirstOrDefault();
            Phase = WindowPhase.Succeeded;
            StatusBarText = "设计时预览：3 张图片已分析";
        }
        else
        {
            Phase = WindowPhase.Empty;
            StatusBarText = "就绪";
        }
    }

    public ObservableCollection<ImageListItemViewModel> Images { get; }

    public ObservableCollection<ImageListItemViewModel> FilteredImages { get; } = [];

    [ObservableProperty]
    private string _imageSearchText = string.Empty;

    [ObservableProperty]
    private bool _isDragOver;

    [ObservableProperty]
    private string _imageStatusFilter = "全部";

    public string ImageCount => Images.Count.ToString();

    public IReadOnlyList<string> ImageStatusFilters { get; } = ["全部", "未识别", "已完成", "失败"];

    partial void OnImageSearchTextChanged(string value) => RefreshFilteredImages();

    private void RefreshFilteredImages()
    {
        var query = ImageSearchText.Trim();
        FilteredImages.Clear();
        foreach (var item in Images)
        {
            var statusOk = ImageStatusFilter switch
            {
                "未识别" => item.AnalysisState is AnalysisState.NotRun or AnalysisState.Stale,
                "已完成" => item.AnalysisState == AnalysisState.Succeeded,
                "失败" => item.AnalysisState is AnalysisState.Failed or AnalysisState.Canceled,
                _ => true,
            };
            if (statusOk && (query.Length == 0 || item.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)))
                FilteredImages.Add(item);
        }
    }

    partial void OnImageStatusFilterChanged(string value) => RefreshFilteredImages();

    public string? CurrentImageId => SelectedImage?.Id;

    public string CurrentFileName => SelectedImage?.FileName ?? "请选择左侧图片";

    public string CurrentFileDetails => SelectedImage is null
        ? string.Empty
        : $"{SelectedImage.DimensionsText} / {SelectedImage.Format} / {SelectedImage.FileSizeText} / {SelectedImage.StatusText}";

    public string BatchProgressText => BatchTotal > 0 ? $"{BatchCompleted}/{BatchTotal}" : string.Empty;

    public event EventHandler<RecognitionRequestedEventArgs>? RecognitionRequested;

    public event EventHandler? CancelRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentImageId))]
    [NotifyPropertyChangedFor(nameof(CurrentFileName))]
    [NotifyPropertyChangedFor(nameof(CurrentFileDetails))]
    private ImageListItemViewModel? _selectedImage;

    [ObservableProperty]
    private bool _isOperationRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchProgressText))]
    private int _batchCompleted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchProgressText))]
    private int _batchTotal;

    /// <summary>
    /// 当前阈值（百分比展示）。唯一来源是当前 Model Pack 的
    /// <c>DefaultThreshold</c>，<see cref="SetModelContext"/> 在模型加载/切换时同步；
    /// 界面不提供调节入口。内部与测试仍可直接赋值（走归一化与联动刷新）。
    /// </summary>
    [ObservableProperty]
    private double _thresholdPercent = 60.94;

    [ObservableProperty]
    private bool _isRightPaneExpanded = true;

    [ObservableProperty]
    private string _currentModelName = "未加载";

    /// <summary>当前实际加载的 Model Pack ID。</summary>
    [ObservableProperty]
    private string? _currentModelId;

    /// <summary>Adds one import result without reordering its successful documents.</summary>
    public void AddImported(ImageImportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        foreach (var document in result.Images)
        {
            var item = new ImageListItemViewModel(document, _thumbnailService);
            item.RefreshAnalysisState(ThresholdPercent / 100);
            Images.Add(item);
        }
        SelectedImage ??= Images.FirstOrDefault();
        Phase = Images.Count == 0 ? WindowPhase.Empty : WindowPhase.Succeeded;
    }

    public bool SelectById(string imageId)
    {
        var item = Images.FirstOrDefault(candidate => candidate.Id == imageId);
        if (item is null)
            return false;
        SelectedImage = item;
        return true;
    }

    public async Task ImportPathsAsync(
        IReadOnlyList<string> paths,
        bool recursive,
        CancellationToken cancellationToken = default)
    {
        if (_imageImportService is null || IsOperationRunning)
            return;

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _activeOperationCancellation = linkedCancellation;
        IsOperationRunning = true;
        StatusBarText = "正在读取图片信息…";
        try
        {
            var result = await _imageImportService
                .ImportAsync(paths, recursive, linkedCancellation.Token)
                .ConfigureAwait(true);
            AddImported(result);
            var importedCount = result.Images.Count;
            var failureCount = result.Failures.Count;
            foreach (var large in result.Failures.Where(f => f.Kind == ImageImportFailureKind.PixelLimitExceeded))
            {
                if (_notifications is null)
                    continue;
                bool confirmed = await _notifications.ConfirmLargeImageAsync(
                    large.Path, large.PixelCount ?? 0, linkedCancellation.Token).ConfigureAwait(true);
                if (!confirmed)
                    continue;
                var retry = await _imageImportService.ImportAsync(
                    [large.Path], false, linkedCancellation.Token,
                    new HashSet<string>([Path.GetFullPath(large.Path)],
                        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal))
                    .ConfigureAwait(true);
                AddImported(retry);
                importedCount += retry.Images.Count;
                failureCount += retry.Failures.Count;
            }
            StatusBarText = failureCount == 0
                ? $"已导入 {importedCount} 张图片"
                : $"已导入 {importedCount} 张，{failureCount} 项失败";
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            StatusBarText = "已取消导入";
        }
        finally
        {
            if (ReferenceEquals(_activeOperationCancellation, linkedCancellation))
                _activeOperationCancellation = null;
            IsOperationRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenImagesAsync()
    {
        if (_platformService is null)
            return;
        var paths = await _platformService
            .PickImageFilesAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (paths is { Count: > 0 })
            await ImportPathsAsync(paths, recursive: false);
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenFolderAsync()
    {
        if (_platformService is null)
            return;
        var path = await _platformService
            .PickFolderAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (path is not null)
            await ImportPathsAsync([path], RecursiveFolderScan);
    }

    [ObservableProperty]
    private bool _recursiveFolderScan;

    [RelayCommand(CanExecute = nameof(CanRecognizeCurrent))]
    private void RecognizeCurrent() => RaiseRecognition(SelectedImage, recognizeAll: false);

    [RelayCommand(CanExecute = nameof(CanRecognizeAll))]
    private void RecognizeAll() => RaiseRecognition(null, recognizeAll: true);

    [RelayCommand(CanExecute = nameof(CanRecognizeItem))]
    private void RecognizeItem(ImageListItemViewModel? item) => RaiseRecognition(item, recognizeAll: false);

    [RelayCommand(CanExecute = nameof(CanUseItemPlatformAction))]
    private async Task CopyPathAsync(ImageListItemViewModel? item)
    {
        if (item is not null && _platformService is not null)
            await _platformService.SetClipboardTextAsync(item.CanonicalPath, CancellationToken.None);
    }

    [RelayCommand(CanExecute = nameof(CanUseItemPlatformAction))]
    private async Task RevealItemAsync(ImageListItemViewModel? item)
    {
        if (item is not null && _platformService is not null)
        {
            var success = await _platformService.RevealInFileManagerAsync(item.CanonicalPath, CancellationToken.None);
            if (!success)
                StatusBarText = "无法在文件管理器中显示该文件";
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseItemPlatformAction))]
    private async Task OpenItemAsync(ImageListItemViewModel? item)
    {
        if (item is not null && _platformService is not null)
        {
            var success = await _platformService.OpenFileAsync(item.CanonicalPath, CancellationToken.None);
            if (!success)
                StatusBarText = "无法使用默认应用打开该文件";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveItem))]
    private void RemoveItem(ImageListItemViewModel? item)
    {
        if (item is null)
            return;
        SelectedImage = item;
        RemoveSelected();
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _activeOperationCancellation?.Cancel();
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleRightPane() => IsRightPaneExpanded = !IsRightPaneExpanded;

    /// <summary>打开设置对话框（由应用层订阅 <see cref="SettingsRequested"/> 并展示窗口）。</summary>
    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>复制当前 Prompt；空 Prompt 不复制并提示（F-05）。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyPrompt))]
    private async Task CopyPromptAsync()
    {
        if (_platformService is null)
            return;
        var copied = await Prompt.CopyAsync(_platformService).ConfigureAwait(true);
        StatusBarText = copied ? "已复制 Prompt" : "没有可复制的 Prompt";
    }

    [RelayCommand(CanExecute = nameof(CanCopyVisibleTags))]
    private async Task CopyVisibleTagsAsync()
    {
        if (_platformService is null) return;
        var text = string.Join(", ", Tags.Groups.SelectMany(g => g.VisibleTags)
            .Where(tag => !tag.IsExcluded).Select(tag => tag.OriginalName));
        await _platformService.SetClipboardTextAsync(text, CancellationToken.None);
        StatusBarText = "已复制可见标签";
    }

    private bool CanCopyVisibleTags() => _platformService is not null && Tags.HitCount > 0;

    private bool CanCopyPrompt() => _platformService is not null && !Prompt.IsEmpty;

    /// <summary>导出同名 .txt：需用户确认路径，不覆盖图片（F-05）。</summary>
    [RelayCommand(CanExecute = nameof(CanExportPrompt))]
    private async Task ExportPromptAsync()
    {
        if (_platformService is null || SelectedImage is null)
            return;
        var exported = await Prompt
            .ExportAsync(_platformService, SelectedImage.CanonicalPath)
            .ConfigureAwait(true);
        StatusBarText = exported ? "已导出 Prompt 文本" : "导出已取消";
    }

    private bool CanExportPrompt() =>
        _platformService is not null && SelectedImage is not null && !Prompt.IsEmpty;

    /// <summary>组内排序下拉的可选项（首版仅置信度降序与字母序，默认置信度降序）。</summary>
    public IReadOnlyList<GroupSortMode> AvailableSortModes { get; } =
    [
        GroupSortMode.ConfidenceDescending,
        GroupSortMode.NameAscending,
    ];

    /// <summary>复制正向提示词（生成信息选项卡）。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyMetadataPrompt))]
    private async Task CopyPositivePromptAsync()
    {
        if (_platformService is null)
            return;
        await _platformService.SetClipboardTextAsync(Metadata.Positive, CancellationToken.None);
        StatusBarText = "已复制正向提示词";
    }

    /// <summary>复制负向提示词（生成信息选项卡）。</summary>
    [RelayCommand(CanExecute = nameof(CanCopyMetadataPrompt))]
    private async Task CopyNegativePromptAsync()
    {
        if (_platformService is null)
            return;
        await _platformService.SetClipboardTextAsync(Metadata.Negative, CancellationToken.None);
        StatusBarText = "已复制负向提示词";
    }

    private bool CanCopyMetadataPrompt() => _platformService is not null && Metadata.IsParsed;

    /// <summary>切换标签的 Prompt 参与状态；只影响 Prompt，不删除标签结果（D-03）。</summary>
    [RelayCommand]
    private void ToggleTag(string? indexText)
    {
        if (!int.TryParse(indexText, out var index))
            return;
        Tags.ToggleExclude(index);
        RefreshPromptOnly();
    }

    /// <summary>清除全部手动覆盖，恢复阈值与规则的自动选择（D-03）。</summary>
    [RelayCommand]
    private void ClearTagOverrides()
    {
        Tags.ClearOverrides();
        RefreshPromptOnly();
    }

    /// <summary>全选可见标签：清除手动排除，使阈值以上标签全部进入 Prompt（D-04）。</summary>
    [RelayCommand]
    private void SelectAllVisibleTags()
    {
        Tags.ClearOverrides();
        RefreshPromptOnly();
        StatusBarText = $"已全选可见标签（{Tags.HitCount} 个）";
    }

    [ObservableProperty]
    private string _newExcludedTag = string.Empty;

    /// <summary>把输入框中的精确原始标签加入排除列表（F-04；非搜索框）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddExcludedTag))]
    private void AddExcludedTag()
    {
        Rules.AddExcludedTag(NewExcludedTag.Trim());
        NewExcludedTag = string.Empty;
    }

    private bool CanAddExcludedTag() => !string.IsNullOrWhiteSpace(NewExcludedTag);

    /// <summary>从排除列表移除一项（F-04）。</summary>
    [RelayCommand]
    private void RemoveExcludedTag(string? tag) => Rules.RemoveExcludedTag(tag);

    [ObservableProperty]
    private string _newReplacementFrom = string.Empty;

    [ObservableProperty]
    private string _newReplacementTo = string.Empty;

    /// <summary>新增精确替换规则（原始标签完全匹配，不支持正则，F-04）。</summary>
    [RelayCommand(CanExecute = nameof(CanAddReplacement))]
    private void AddReplacement()
    {
        Rules.SetReplacement(NewReplacementFrom.Trim(), NewReplacementTo);
        NewReplacementFrom = string.Empty;
        NewReplacementTo = string.Empty;
    }

    private bool CanAddReplacement() => !string.IsNullOrWhiteSpace(NewReplacementFrom);

    /// <summary>移除一项精确替换（F-04）。</summary>
    [RelayCommand]
    private void RemoveReplacement(string? original) => Rules.RemoveReplacement(original);

    partial void OnNewExcludedTagChanged(string value) => AddExcludedTagCommand.NotifyCanExecuteChanged();

    partial void OnNewReplacementFromChanged(string value) => AddReplacementCommand.NotifyCanExecuteChanged();

    /// <summary>规则编辑后只重算 Prompt（阈值/目录/快照不变）。</summary>
    private void RefreshPromptOnly()
    {
        CurrentPromptSettings = Rules.Current;
        Prompt.SetInputs(
            CurrentCatalog,
            SelectedImage?.Document.Prediction,
            CurrentSelection,
            ThresholdPercent / 100,
            CurrentPromptSettings,
            CurrentGroups);
        CopyVisibleTagsCommand.NotifyCanExecuteChanged();
    }

    public void BeginBatch(int total)
    {
        if (total < 0)
            throw new ArgumentOutOfRangeException(nameof(total));
        BatchCompleted = 0;
        BatchTotal = total;
        IsOperationRunning = total > 0;
        Phase = total > 0 ? WindowPhase.Busy : Phase;
        StatusBarText = total > 0 ? "正在识别…" : "就绪";
    }

    public void ReportBatchProgress(int completed)
    {
        BatchCompleted = Math.Clamp(completed, 0, BatchTotal);
    }

    public void EndBatch(bool canceled = false)
    {
        IsOperationRunning = false;
        Phase = Images.Count == 0 ? WindowPhase.Empty : WindowPhase.Succeeded;
        StatusBarText = canceled ? "识别已取消" : "识别完成";
    }

    private void RaiseRecognition(ImageListItemViewModel? item, bool recognizeAll)
    {
        if (!recognizeAll && item is null)
            return;
        RecognitionRequested?.Invoke(
            this,
            new RecognitionRequestedEventArgs(recognizeAll, item?.Id));
    }

    [RelayCommand(CanExecute = nameof(CanSelectPrevious))]
    private void SelectPrevious()
    {
        var index = SelectedImage is null ? -1 : Images.IndexOf(SelectedImage);
        if (index > 0)
            SelectedImage = Images[index - 1];
    }

    [RelayCommand(CanExecute = nameof(CanSelectNext))]
    private void SelectNext()
    {
        var index = SelectedImage is null ? -1 : Images.IndexOf(SelectedImage);
        if (index >= 0 && index < Images.Count - 1)
            SelectedImage = Images[index + 1];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemoveSelected()
    {
        if (SelectedImage is null)
            return;
        var index = Images.IndexOf(SelectedImage);
        var removed = SelectedImage;
        Images.RemoveAt(index);
        _imageImportService?.Forget(removed.CanonicalPath);
        removed.Dispose();
        SelectedImage = Images.Count == 0 ? null : Images[Math.Min(index, Images.Count - 1)];
        Phase = Images.Count == 0 ? WindowPhase.Empty : Phase;
    }

    [RelayCommand(CanExecute = nameof(HasImages))]
    private void ClearImages()
    {
        foreach (var image in Images)
            image.Dispose();
        Images.Clear();
        _imageImportService?.ClearKnownPaths();
        SelectedImage = null;
        Phase = WindowPhase.Empty;
    }

    private bool HasSelection() => SelectedImage is not null && !IsOperationRunning;

    private bool HasImages() => Images.Count > 0 && !IsOperationRunning;

    private bool CanOpen() => _platformService is not null && _imageImportService is not null && !IsOperationRunning;

    private bool CanRecognizeCurrent() => SelectedImage is not null && !IsOperationRunning;

    private bool CanRecognizeAll() => Images.Count > 0 && !IsOperationRunning;

    private bool CanRecognizeItem(ImageListItemViewModel? item) => item is not null && !IsOperationRunning;

    private bool CanUseItemPlatformAction(ImageListItemViewModel? item) => item is not null && _platformService is not null;

    private bool CanRemoveItem(ImageListItemViewModel? item) => item is not null && !IsOperationRunning;

    private bool CanCancel() => IsOperationRunning;

    private bool CanSelectPrevious() =>
        SelectedImage is not null && Images.IndexOf(SelectedImage) > 0;

    private bool CanSelectNext()
    {
        var index = SelectedImage is null ? -1 : Images.IndexOf(SelectedImage);
        return index >= 0 && index < Images.Count - 1;
    }

    /// <summary>设置当前模型的共享目录、分组、阈值与规则（模型加载/切换时调用）。</summary>
    public void SetModelContext(
        TagCatalog? catalog,
        IReadOnlyList<Core.ModelPacks.GroupDescriptor>? groups,
        PromptSettings? promptSettings,
        double defaultThreshold)
    {
        CurrentCatalog = catalog;
        CurrentGroups = groups ?? [];
        if (promptSettings is not null)
            CurrentPromptSettings = promptSettings;
        ThresholdPercent = Math.Clamp(double.IsFinite(defaultThreshold) ? defaultThreshold : 0, 0, 1) * 100;
        ResetRules();
        SyncChildViewModels();
    }

    /// <summary>按当前分组与规则重建右侧编辑器（模型切换时调用；规则改动经 <see cref="AttachRules"/> 回流）。</summary>
    public void ResetRules()
    {
        DetachRules(Rules);
        var fresh = new PromptRulesViewModel(CurrentPromptSettings, CurrentGroups, _promptStore);
        AttachRules(fresh);
        Rules = fresh;
        OnPropertyChanged(nameof(Rules));
    }

    /// <summary>订阅规则编辑器的 <c>Current</c> 变化并回流到预览与持久化（编辑器内部已防抖保存）。</summary>
    private void AttachRules(PromptRulesViewModel rules)
    {
        rules.PropertyChanged += OnRulesPropertyChanged;
        rules.Groups.CollectionChanged += OnRuleGroupsCollectionChanged;
        foreach (var row in rules.Groups)
            row.PropertyChanged += OnRulesPropertyChanged;
    }

    private void DetachRules(PromptRulesViewModel rules)
    {
        rules.PropertyChanged -= OnRulesPropertyChanged;
        rules.Groups.CollectionChanged -= OnRuleGroupsCollectionChanged;
        foreach (var row in rules.Groups)
            row.PropertyChanged -= OnRulesPropertyChanged;
    }

    private void OnRuleGroupsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (GroupRuleRow row in e.NewItems)
                row.PropertyChanged += OnRulesPropertyChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (GroupRuleRow row in e.OldItems)
                row.PropertyChanged -= OnRulesPropertyChanged;
        }
        RefreshRulesFromEditor();
    }

    private void OnRulesPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(PromptRulesViewModel.Current), StringComparison.Ordinal))
            return;
        RefreshRulesFromEditor();
    }

    /// <summary>把编辑器当前规则同步为 <see cref="CurrentPromptSettings"/> 并重算 Prompt 预览。</summary>
    public void RefreshRulesFromEditor()
    {
        CurrentPromptSettings = Rules.Current;
        Prompt.SetInputs(
            CurrentCatalog,
            SelectedImage?.Document.Prediction,
            CurrentSelection,
            ThresholdPercent / 100,
            CurrentPromptSettings,
            CurrentGroups);
    }

    partial void OnSelectedImageChanged(ImageListItemViewModel? value)
    {
        NotifySessionCommandStates();
        SyncChildViewModels();
        CopyPromptCommand.NotifyCanExecuteChanged();
        ExportPromptCommand.NotifyCanExecuteChanged();
        // 元数据懒加载：首次成为当前项时异步解析并会话缓存；VM 内部按图片 ID 丢弃迟到结果。
        _ = Metadata.SetCurrentAsync(value?.Document);
    }

    /// <summary>
    /// 将当前选择、阈值与模型上下文同步到 Tags/Metadata/Prompt 子 VM。
    /// 切图时重建选择并原子切换三者来源，不沿用上一张数据；阈值变化不清除手动排除。
    /// </summary>
    public void SyncChildViewModels()
    {
        var threshold = ThresholdPercent / 100;
        var prediction = SelectedImage?.Document.Prediction;
        CurrentSelection = new TagSelection();
        Tags.SetInputs(CurrentCatalog, prediction, threshold, CurrentSelection, CurrentGroups);
        Prompt.SetInputs(CurrentCatalog, prediction, CurrentSelection, threshold, CurrentPromptSettings, CurrentGroups);
        OnPropertyChanged(nameof(CurrentSelection));
        CopyVisibleTagsCommand.NotifyCanExecuteChanged();
    }

    private void NotifySessionCommandStates()
    {
        SelectPreviousCommand.NotifyCanExecuteChanged();
        SelectNextCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ClearImagesCommand.NotifyCanExecuteChanged();
        RecognizeCurrentCommand.NotifyCanExecuteChanged();
        RecognizeAllCommand.NotifyCanExecuteChanged();
        RecognizeItemCommand.NotifyCanExecuteChanged();
        CopyPathCommand.NotifyCanExecuteChanged();
        RevealItemCommand.NotifyCanExecuteChanged();
        OpenItemCommand.NotifyCanExecuteChanged();
        RemoveItemCommand.NotifyCanExecuteChanged();
        CopyPromptCommand.NotifyCanExecuteChanged();
        ExportPromptCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOperationRunningChanged(bool value)
    {
        OpenImagesCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        RecognizeCurrentCommand.NotifyCanExecuteChanged();
        RecognizeAllCommand.NotifyCanExecuteChanged();
        RecognizeItemCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        ClearImagesCommand.NotifyCanExecuteChanged();
        RemoveItemCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    partial void OnThresholdPercentChanged(double value)
    {
        var normalized = Math.Clamp(double.IsFinite(value) ? value : 60.94, 0, 100);
        if (normalized != value)
        {
            ThresholdPercent = normalized;
            return;
        }

        foreach (var image in Images)
            image.RefreshAnalysisState(value / 100);
        Tags.UpdateThreshold(value / 100);
        Prompt.SetInputs(CurrentCatalog, SelectedImage?.Document.Prediction, CurrentSelection, value / 100, CurrentPromptSettings, CurrentGroups);
        OnPropertyChanged(nameof(CurrentFileDetails));
    }

    /// <summary>True only in the XAML designer; the phase-cycling preview button hides in the real app.</summary>
    public bool IsDesignMode => Design.IsDesignMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(IsSucceeded))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    private WindowPhase _phase;

    [ObservableProperty]
    private string _statusBarText;

    public bool IsEmpty => Phase == WindowPhase.Empty;
    public bool IsSucceeded => Phase == WindowPhase.Succeeded;
    public bool IsBusy => Phase == WindowPhase.Busy;
    public bool IsError => Phase == WindowPhase.Error;

    /// <summary>Index into <see cref="ThemeModes"/>; order matches <see cref="ThemeMode"/> (System, Light, Dark).</summary>
    public IReadOnlyList<string> ThemeModes { get; } = ["跟随系统", "浅色", "深色"];

    [ObservableProperty]
    private int _selectedThemeIndex;

    partial void OnSelectedThemeIndexChanged(int value)
    {
        if (!Design.IsDesignMode)
            _themeWatcher?.SwitchTheme((ThemeMode)value);
    }

    /// <summary>Design-time helper: cycles Empty → Succeeded → Busy → Error.</summary>
    [RelayCommand]
    private void CyclePhase()
    {
        Phase = (WindowPhase)(((int)Phase + 1) % 4);
        StatusBarText = $"设计时预览：{Phase}";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _activeOperationCancellation?.Cancel();
        _activeOperationCancellation?.Dispose();
        _activeOperationCancellation = null;
        foreach (var image in Images)
            image.Dispose();
        Images.Clear();
        DetachRules(Rules);
        try
        {
            DialogManager.Dispose();
        }
        catch (Exception)
        {
            // 关闭时的释放失败不影响退出流程。
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Menu entry point; parameter is the ThemeMode integer as string.</summary>
    [RelayCommand]
    private void SetTheme(string? modeIndex)
    {
        if (int.TryParse(modeIndex, out var index))
            SelectedThemeIndex = index;
    }
}

public sealed class RecognitionRequestedEventArgs(bool recognizeAll, string? imageId) : EventArgs
{
    public bool RecognizeAll { get; } = recognizeAll;

    public string? ImageId { get; } = imageId;
}
