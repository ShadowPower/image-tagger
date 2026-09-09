using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;

namespace ImageTagger.App.ViewModels;

/// <summary>单个可见标签的只读投影（VM 层轻量包装，不复制目录文本）。</summary>
public sealed partial class TagItemViewModel : ObservableObject
{
    public TagItemViewModel(TagCatalogEntry catalogEntry, float probability, bool isExcluded, bool isTopRating)
    {
        CatalogEntry = catalogEntry ?? throw new ArgumentNullException(nameof(catalogEntry));
        Probability = probability;
        IsExcluded = isExcluded;
        IsTopRating = isTopRating;
    }

    public TagCatalogEntry CatalogEntry { get; }

    public float Probability { get; }

    public int Index => CatalogEntry.Index;

    public string OriginalName => CatalogEntry.OriginalName;

    /// <summary>中文翻译展示：缺失、为空或与原文相同时显示“暂无翻译”。</summary>
    public string TranslationDisplay =>
        string.IsNullOrEmpty(CatalogEntry.ChineseTranslation) ||
        string.Equals(CatalogEntry.ChineseTranslation, CatalogEntry.OriginalName, StringComparison.Ordinal)
            ? "暂无翻译"
            : CatalogEntry.ChineseTranslation;

    public string TranslationText => ShowTranslation ? TranslationDisplay : string.Empty;

    public string ConfidenceDisplay => ShowTranslation && !string.IsNullOrEmpty(TranslationText)
        ? $" {ConfidenceText}" : $" {ConfidenceText}";

    public bool ShowTranslation { get; set; } = true;

    /// <summary>两位小数置信度，如 98.73%。</summary>
    public string ConfidenceText => $"{Probability * 100:F2}%";

    [ObservableProperty]
    private bool _isExcluded;

    [ObservableProperty]
    private bool _isTopRating;
}

/// <summary>单个分组的只读视图：分组标识、显示名、可见标签、计数与空状态。</summary>
public sealed partial class TagGroupViewModel : ObservableObject
{
    public TagGroupViewModel(string groupId, string displayName, IReadOnlyList<TagItemViewModel> visibleTags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        GroupId = groupId;
        DisplayName = string.IsNullOrEmpty(displayName) ? groupId : displayName;
        VisibleTags = visibleTags ?? throw new ArgumentNullException(nameof(visibleTags));
    }

    public string GroupId { get; }

    public string DisplayName { get; }

    public IReadOnlyList<TagItemViewModel> VisibleTags { get; }

    public int Count => VisibleTags.Count;

    public bool IsEmpty => Count == 0;
}

/// <summary>
/// 识别标签选项卡 ViewModel：输入 Catalog + 快照 + 阈值 + 选择，
/// 输出按 manifest groups 顺序的分组视图。阈值变化只重算投影，不重跑模型。
/// </summary>
public sealed partial class TagsViewModel : ObservableObject
{
    private const string RatingGroupId = "rating";
    private const double DefaultThresholdValue = 0.6094;

    private TagCatalog? _catalog;
    private PredictionSnapshot? _prediction;
    private TagSelection _selection = new();
    private IReadOnlyList<GroupDescriptor>? _manifestGroups;

    private double _threshold = DefaultThresholdValue;

    [ObservableProperty]
    private bool _showChineseTranslation = true;

    public TagsViewModel()
    {
    }

    public ObservableCollection<TagGroupViewModel> Groups { get; } = [];

    /// <summary>当前阈值（0..1），变化时同步重算投影。</summary>
    public double Threshold
    {
        get => _threshold;
        set
        {
            if (!double.IsFinite(value) || value is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (SetProperty(ref _threshold, value))
                Rebuild();
        }
    }

    [ObservableProperty]
    private int _hitCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private double _inferenceMs;

    [ObservableProperty]
    private string _runtime = string.Empty;

    [ObservableProperty]
    private string _provider = string.Empty;

    [ObservableProperty]
    private string _device = string.Empty;

    /// <summary>是否有可展示的推理结果（目录与快照齐备且长度一致）；无结果时选项卡显示统一空状态（D-04）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnrecognized))]
    private bool _hasResult;

    /// <summary>尚未识别此图片（无结果可展示）。</summary>
    public bool IsUnrecognized => !HasResult;

    /// <summary>当前选择对象（与 Prompt 构建器共享引用）。</summary>
    public TagSelection Selection => _selection;

    /// <summary>设置全部输入并同步重算（阈值变化不重跑模型，仅重算投影）。</summary>
    public void SetInputs(
        TagCatalog? catalog,
        PredictionSnapshot? prediction,
        double threshold,
        TagSelection? selection,
        IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _catalog = catalog;
        _prediction = prediction;
        _selection = selection ?? new TagSelection();
        _manifestGroups = manifestGroups;
        // 直接写字段后一次性重算，避免中间状态触发多次重算。
        SetProperty(ref _threshold, threshold, nameof(Threshold));
        Rebuild();
    }

    /// <summary>仅更新阈值，不清除手动排除。</summary>
    public void UpdateThreshold(double threshold) => Threshold = threshold;

    partial void OnShowChineseTranslationChanged(bool value) => Rebuild();

    /// <summary>切换指定索引的手动排除状态；越界索引直接忽略。</summary>
    public void ToggleExclude(int index)
    {
        if (_catalog is null || index < 0 || index >= _catalog.Count)
            return;
        if (!_selection.ForceExcludedIndices.Remove(index))
            _selection.ForceExcludedIndices.Add(index);
        Rebuild();
    }

    /// <summary>清除全部手动覆盖，恢复阈值与规则的自动选择。</summary>
    public void ClearOverrides()
    {
        if (_selection.ForceExcludedIndices.Count == 0)
            return;
        _selection.ForceExcludedIndices.Clear();
        Rebuild();
    }

    /// <summary>
    /// 应用新推理结果（同目录仅换快照；换模型时同时换目录与分组）。
    /// 按标签名保留手动排除：旧排除名在新目录同名索引继续排除，不匹配则丢弃。
    /// </summary>
    public void ApplyNewPrediction(
        PredictionSnapshot? prediction,
        TagCatalog? catalog,
        IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        var oldNames = new List<string>(_selection.ForceExcludedIndices.Count);
        if (_catalog is not null)
        {
            foreach (var index in _selection.ForceExcludedIndices)
            {
                if (index >= 0 && index < _catalog.Count)
                    oldNames.Add(_catalog[index].OriginalName);
            }
        }

        _catalog = catalog;
        _prediction = prediction;
        _manifestGroups = manifestGroups;
        _selection.ForceExcludedIndices.Clear();
        if (catalog is not null)
        {
            foreach (var name in oldNames)
            {
                if (catalog.TryGetIndex(name, out var newIndex))
                    _selection.ForceExcludedIndices.Add(newIndex);
            }
        }
        Rebuild();
    }

    /// <summary>同目录换快照的便捷重载（保留当前排除，不做名称映射）。</summary>
    public void ApplyNewPrediction(PredictionSnapshot? prediction)
    {
        _prediction = prediction;
        Rebuild();
    }

    private void Rebuild()
    {
        Groups.Clear();

        if (_catalog is null || _prediction is null || _prediction.Probabilities.Length != _catalog.Count)
        {
            HitCount = 0;
            TotalCount = _catalog?.Count ?? 0;
            InferenceMs = 0;
            Runtime = string.Empty;
            Provider = string.Empty;
            Device = string.Empty;
            HasResult = false;
            return;
        }

        HasResult = true;

        var catalog = _catalog;
        var probabilities = _prediction.Probabilities;
        var ordered = ResolveGroupOrder(catalog, _manifestGroups);

        int hits = 0;
        foreach (var (groupId, displayName, strategy) in ordered)
        {
            List<TagItemViewModel> items;
            if (string.Equals(strategy, "rating", StringComparison.Ordinal)
                || string.Equals(groupId, RatingGroupId, StringComparison.Ordinal))
            {
                // 分级组固定展示全部项（按概率降序），标记最高项。
                items = BuildRatingItems(catalog, probabilities);
            }
            else
            {
                items = BuildThresholdItems(catalog, probabilities, groupId, _threshold);
            }
            hits += items.Count;
            Groups.Add(new TagGroupViewModel(groupId, displayName, items));
        }

        HitCount = hits;
        TotalCount = catalog.Count;
        InferenceMs = _prediction.Duration.TotalMilliseconds;
        Runtime = _prediction.Runtime ?? string.Empty;
        Provider = _prediction.ExecutionProvider ?? string.Empty;
        Device = _prediction.ExecutionProvider ?? string.Empty;
    }

    private static List<(string GroupId, string DisplayName, string Strategy)> ResolveGroupOrder(
        TagCatalog catalog,
        IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        var ordered = new List<(string GroupId, string DisplayName, string Strategy)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (manifestGroups is { Count: > 0 })
        {
            // 按 manifest groups 顺序渲染标签页。
            foreach (var group in manifestGroups)
            {
                if (group is null || string.IsNullOrEmpty(group.Id) || !seen.Add(group.Id))
                    continue;
                ordered.Add((group.Id, string.IsNullOrEmpty(group.DisplayName) ? group.Id : group.DisplayName,
                    string.IsNullOrWhiteSpace(group.Strategy) ? "threshold" : group.Strategy));
            }
            // 补齐目录中有但 manifest 未声明的分组（首次出现顺序）。
            foreach (var entry in catalog.Entries)
            {
                if (!seen.Add(entry.DisplayGroup))
                    continue;
                ordered.Add((entry.DisplayGroup, entry.DisplayGroup,
                    string.Equals(entry.DisplayGroup, RatingGroupId, StringComparison.Ordinal) ? "rating" : "threshold"));
            }
            return ordered;
        }

        foreach (var entry in catalog.Entries)
        {
            if (!seen.Add(entry.DisplayGroup))
                continue;
            ordered.Add((entry.DisplayGroup, entry.DisplayGroup,
                string.Equals(entry.DisplayGroup, RatingGroupId, StringComparison.Ordinal) ? "rating" : "threshold"));
        }
        return ordered;
    }

    private List<TagItemViewModel> BuildRatingItems(TagCatalog catalog, float[] probabilities)
    {
        // 分级是单选分类：只展示最高概率的一项（平分按模型索引升序），标记为最可能。
        // 不受阈值过滤：低置信度的落选分级不再展示，未达阈值的落选不会淹没结果。
        int bestIndex = -1;
        float bestProb = float.NegativeInfinity;
        for (int i = 0; i < catalog.Count; i++)
        {
            if (!string.Equals(catalog[i].DisplayGroup, RatingGroupId, StringComparison.Ordinal))
                continue;
            float prob = probabilities[i];
            if (!float.IsFinite(prob))
                continue;
            if (bestIndex < 0 || prob > bestProb || (prob == bestProb && i < bestIndex))
            {
                bestIndex = i;
                bestProb = prob;
            }
        }
        if (bestIndex < 0)
            return [];
        return
        [
            new TagItemViewModel(
                catalog[bestIndex],
                probabilities[bestIndex],
                _selection.ForceExcludedIndices.Contains(bestIndex),
                true)
            { ShowTranslation = ShowChineseTranslation },
        ];
    }

    private List<TagItemViewModel> BuildThresholdItems(
        TagCatalog catalog,
        float[] probabilities,
        string groupId,
        double threshold)
    {
        // 其余组应用 >= 阈值（含边界），组内置信度降序、平分索引升序。
        var visible = new List<(int Index, float Prob)>();
        for (int i = 0; i < catalog.Count; i++)
        {
            var entry = catalog[i];
            if (!string.Equals(entry.DisplayGroup, groupId, StringComparison.Ordinal))
                continue;
            float prob = probabilities[i];
            if (!float.IsFinite(prob) || (double)prob < threshold)
                continue;
            visible.Add((i, prob));
        }
        visible.Sort(static (a, b) =>
        {
            int confidence = b.Prob.CompareTo(a.Prob);
            return confidence != 0 ? confidence : a.Index.CompareTo(b.Index);
        });

        var items = new List<TagItemViewModel>(visible.Count);
        foreach (var (index, prob) in visible)
        {
            items.Add(new TagItemViewModel(
                catalog[index],
                prob,
                _selection.ForceExcludedIndices.Contains(index),
                false)
            { ShowTranslation = ShowChineseTranslation });
        }
        return items;
    }
}
