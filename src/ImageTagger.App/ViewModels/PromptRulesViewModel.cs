using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using ImageTagger.Core.Services;

namespace ImageTagger.App.ViewModels;

/// <summary>分组规则行：分组标识与显示名只读，启用开关与组内排序可写。</summary>
public sealed partial class GroupRuleRow : ObservableObject
{
    /// <summary>创建分组规则行；显示名缺失时回落为分组标识。</summary>
    public GroupRuleRow(string groupId, string displayName, bool enabled, GroupSortMode sortMode)
    {
        ArgumentException.ThrowIfNullOrEmpty(groupId);
        GroupId = groupId;
        DisplayName = string.IsNullOrEmpty(displayName) ? groupId : displayName;
        _enabled = enabled;
        _sortMode = sortMode;
    }

    /// <summary>分组稳定标识（只读）。</summary>
    public string GroupId { get; }

    /// <summary>分组显示名：取 manifest 中文显示名，找不到则用分组标识（只读）。</summary>
    public string DisplayName { get; }

    /// <summary>分组是否启用（可写）。</summary>
    [ObservableProperty]
    private bool _enabled;

    /// <summary>组内排序模式（可写）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortModeDescription))]
    private GroupSortMode _sortMode;

    /// <summary>完整排序说明，避免仅凭符号猜测规则。</summary>
    public string SortModeDescription => SortMode == GroupSortMode.NameAscending
        ? "按名称升序"
        : "按置信度降序";
}

/// <summary>
/// Prompt 规则编辑 ViewModel：供右侧规则区绑定，无 UI 框架引用。
/// 每次改动后经存储防抖保存（不 flush），<see cref="Flush"/> 立即落盘。
/// </summary>
public sealed partial class PromptRulesViewModel : ObservableObject
{
    private const double MinWeightLimit = 0.5;
    private const double MaxWeightLimit = 5.0;
    private const int MinTagsLimit = 1;
    private const int MaxTagsLimit = 500;

    private readonly IPromptSettingsStore? _store;
    private readonly IReadOnlyList<GroupDescriptor>? _manifestGroups;
    private readonly Dictionary<string, double> _groupThresholds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _replacements = new(StringComparer.Ordinal);
    private bool _suppressPersist;

    private string _tagSeparator = ", ";
    private string _groupSeparator = ", ";
    private string _prefix = string.Empty;
    private string _suffix = string.Empty;
    private int _maxTags = 100;
    private double _minWeight = 1.00;
    private double _maxWeight = 1.30;

    /// <summary>
    /// 创建规则编辑器；initial 为空引用时使用工厂默认。
    /// manifestGroups 用于显示名与补齐新增组。
    /// </summary>
    public PromptRulesViewModel(
        PromptSettings? initial,
        IReadOnlyList<GroupDescriptor>? manifestGroups,
        IPromptSettingsStore? store = null)
    {
        _manifestGroups = manifestGroups;
        _store = store;
        _suppressPersist = true;
        try
        {
            ApplyAll(initial ?? PromptSettingsFactory.CreateDefault());
        }
        finally
        {
            _suppressPersist = false;
        }
        Groups.CollectionChanged += OnGroupsChanged;
        ExcludedTags.CollectionChanged += OnExcludedTagsChanged;
    }

    /// <summary>分组行集合，行顺序即 Prompt 分组顺序。</summary>
    public ObservableCollection<GroupRuleRow> Groups { get; } = [];

    /// <summary>排除标签精确列表（可写集合，每行一个原始标签）。</summary>
    public ObservableCollection<string> ExcludedTags { get; } = [];

    /// <summary>分组阈值覆盖的可编辑视图。</summary>
    public IReadOnlyDictionary<string, double> GroupThresholds => _groupThresholds;

    /// <summary>精确替换规则的可编辑视图（原始标签到输出文本）。</summary>
    public IReadOnlyDictionary<string, string> Replacements => _replacements;

    /// <summary>是否使用全局阈值（可写）。</summary>
    [ObservableProperty]
    private bool _useGlobalThreshold = true;

    /// <summary>下划线转空格（可写）。</summary>
    [ObservableProperty]
    private bool _underscoreToSpace = true;

    [ObservableProperty]
    private bool _escapeParentheses = true;

    [ObservableProperty]
    private QualityTagPreset _qualityPreset;

    [ObservableProperty]
    private string _qualityCustomTags = string.Empty;

    public IReadOnlyList<string> QualityPresetNames { get; } =
        ["不添加", "Stable Diffusion", "SDXL", "Pony", "Illustrious", "NoobAI", "Anima", "自定义"];

    public bool IsCustomQualityPreset => QualityPreset == QualityTagPreset.Custom;

    public int QualityPresetIndex
    {
        get => (int)QualityPreset;
        set => QualityPreset = (QualityTagPreset)Math.Clamp(value, 0, QualityPresetNames.Count - 1);
    }

    /// <summary>是否启用概率权重（可写）。</summary>
    [ObservableProperty]
    private bool _probabilityWeights;

    /// <summary>输出是否保留尾部分隔符（可写）。</summary>
    [ObservableProperty]
    private bool _trailingSeparator;

    /// <summary>标签分隔符（可写，空字符串忽略并保持旧值）。</summary>
    public string TagSeparator
    {
        get => _tagSeparator;
        set
        {
            if (string.IsNullOrEmpty(value))
                return;
            if (SetProperty(ref _tagSeparator, value))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>分组分隔符（可写，空字符串忽略并保持旧值）。</summary>
    public string GroupSeparator
    {
        get => _groupSeparator;
        set
        {
            if (string.IsNullOrEmpty(value))
                return;
            if (SetProperty(ref _groupSeparator, value))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>输出前缀（可写，允许空）。</summary>
    public string Prefix
    {
        get => _prefix;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _prefix, value))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>输出后缀（可写，允许空）。</summary>
    public string Suffix
    {
        get => _suffix;
        set
        {
            value ??= string.Empty;
            if (SetProperty(ref _suffix, value))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>最大标签数（可写，钳制到 1..500）。</summary>
    public int MaxTags
    {
        get => _maxTags;
        set
        {
            int clamped = Math.Clamp(value, MinTagsLimit, MaxTagsLimit);
            if (SetProperty(ref _maxTags, clamped))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>权重下限（可写，范围 0.5..5，超过上限时钳制为合法值）。</summary>
    public double MinWeight
    {
        get => _minWeight;
        set
        {
            if (!double.IsFinite(value))
                return;
            double clamped = Math.Clamp(value, MinWeightLimit, MaxWeightLimit);
            if (clamped > _maxWeight)
                clamped = _maxWeight;
            if (SetProperty(ref _minWeight, clamped))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>权重上限（可写，范围 0.5..5，低于下限时钳制为合法值）。</summary>
    public double MaxWeight
    {
        get => _maxWeight;
        set
        {
            if (!double.IsFinite(value))
                return;
            double clamped = Math.Clamp(value, MinWeightLimit, MaxWeightLimit);
            if (clamped < _minWeight)
                clamped = _minWeight;
            if (SetProperty(ref _maxWeight, clamped))
            {
                OnPropertyChanged(nameof(Current));
                Persist();
            }
        }
    }

    /// <summary>当前完整规则快照：分组顺序即行顺序，各集合返回副本。</summary>
    public PromptSettings Current => new()
    {
        GroupRules = Groups.Select(static row => new GroupRule
        {
            GroupId = row.GroupId,
            Enabled = row.Enabled,
            SortMode = row.SortMode,
        }).ToList(),
        Thresholds = new ThresholdRules
        {
            UseGlobalThreshold = UseGlobalThreshold,
            GroupThresholds = new Dictionary<string, double>(_groupThresholds, StringComparer.Ordinal),
        },
        Transforms = new TransformRules
        {
            UnderscoreToSpace = UnderscoreToSpace,
            EscapeParentheses = EscapeParentheses,
            ExcludedTags = ExcludedTags.ToList(),
            Replacements = new Dictionary<string, string>(_replacements, StringComparer.Ordinal),
            ProbabilityWeights = ProbabilityWeights,
            MinWeight = MinWeight,
            MaxWeight = MaxWeight,
        },
        Output = new OutputRules
        {
            TagSeparator = TagSeparator,
            GroupSeparator = GroupSeparator,
            Prefix = Prefix,
            Suffix = Suffix,
            MaxTags = MaxTags,
            TrailingSeparator = TrailingSeparator,
            QualityPreset = QualityPreset,
            QualityCustomTags = QualityCustomTags,
        },
    };

    /// <summary>上移分组；越界或不存在时忽略。</summary>
    [RelayCommand]
    public void MoveGroupUp(string? groupId)
    {
        var row = FindRow(groupId);
        if (row is null)
            return;
        int index = Groups.IndexOf(row);
        if (index > 0)
            Groups.Move(index, index - 1);
    }

    /// <summary>下移分组；越界或不存在时忽略。</summary>
    [RelayCommand]
    public void MoveGroupDown(string? groupId)
    {
        var row = FindRow(groupId);
        if (row is null)
            return;
        int index = Groups.IndexOf(row);
        if (index >= 0 && index < Groups.Count - 1)
            Groups.Move(index, index + 1);
    }

    /// <summary>翻转分组启用状态；分组不存在时忽略。</summary>
    [RelayCommand]
    public void ToggleGroup(string? groupId)
    {
        var row = FindRow(groupId);
        if (row is null)
            return;
        row.Enabled = !row.Enabled;
    }

    /// <summary>设置组内排序，参数形如 "general|ConfidenceDescending"；非法输入忽略。</summary>
    [RelayCommand]
    public void SetGroupSort(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
            return;
        int separator = parameter.LastIndexOf('|');
        if (separator <= 0 || separator == parameter.Length - 1)
            return;
        string groupId = parameter.Substring(0, separator);
        string modeText = parameter.Substring(separator + 1);
        if (!Enum.TryParse<GroupSortMode>(modeText, ignoreCase: true, out var mode))
            return;
        SetGroupSortMode(groupId, mode);
    }

    /// <summary>直接设置组内排序；分组不存在时忽略。</summary>
    public void SetGroupSortMode(string? groupId, GroupSortMode mode)
    {
        var row = FindRow(groupId);
        if (row is null)
            return;
        row.SortMode = mode;
    }

    /// <summary>设置分组阈值覆盖（范围 0..1）；分组非法或越界值忽略。</summary>
    public void SetGroupThreshold(string? groupId, double value)
    {
        if (string.IsNullOrEmpty(groupId) || !IsThresholdInRange(value))
            return;
        if (_groupThresholds.TryGetValue(groupId, out var current) && current == value)
            return;
        _groupThresholds[groupId] = value;
        OnPropertyChanged(nameof(GroupThresholds));
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    /// <summary>清除分组阈值覆盖，回到全局或模型默认。</summary>
    public void ClearGroupThreshold(string? groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            return;
        if (!_groupThresholds.Remove(groupId))
            return;
        OnPropertyChanged(nameof(GroupThresholds));
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    /// <summary>预览用有效阈值：全局开关开启或无覆盖时取模型默认。</summary>
    public double EffectiveThreshold(string? groupId, double modelDefault)
    {
        if (!UseGlobalThreshold
            && !string.IsNullOrEmpty(groupId)
            && _groupThresholds.TryGetValue(groupId, out var value))
            return value;
        return modelDefault;
    }

    /// <summary>新增排除标签；空值或重复忽略。</summary>
    public void AddExcludedTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || ExcludedTags.Contains(tag))
            return;
        ExcludedTags.Add(tag);
    }

    /// <summary>移除排除标签；不存在时忽略。</summary>
    public void RemoveExcludedTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return;
        ExcludedTags.Remove(tag);
    }

    /// <summary>新增或更新精确替换；空原始标签忽略。</summary>
    public void SetReplacement(string? original, string? output)
    {
        if (string.IsNullOrEmpty(original))
            return;
        output ??= string.Empty;
        if (_replacements.TryGetValue(original, out var current)
            && string.Equals(current, output, StringComparison.Ordinal))
            return;
        _replacements[original] = output;
        OnPropertyChanged(nameof(Replacements));
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    /// <summary>移除精确替换；不存在时忽略。</summary>
    public void RemoveReplacement(string? original)
    {
        if (string.IsNullOrEmpty(original))
            return;
        if (!_replacements.Remove(original))
            return;
        OnPropertyChanged(nameof(Replacements));
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    /// <summary>回到 manifest 逆序默认（主题在前、分级最后，全启用、置信度降序）；仅重置规则，不碰其它应用设置。</summary>
    [RelayCommand]
    public void ResetDefaults()
    {
        _suppressPersist = true;
        try
        {
            ApplyAll(PromptSettingsFactory.CreateDefault(_manifestGroups));
        }
        finally
        {
            _suppressPersist = false;
        }
        Persist();
    }

    /// <summary>立即保存当前规则并要求落盘。</summary>
    public void Flush() => _store?.Save(Current, flush: true);

    partial void OnUseGlobalThresholdChanged(bool value)
    {
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    partial void OnUnderscoreToSpaceChanged(bool value)
    {
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    partial void OnEscapeParenthesesChanged(bool value) { OnPropertyChanged(nameof(Current)); Persist(); }
    partial void OnQualityPresetChanged(QualityTagPreset value) { OnPropertyChanged(nameof(QualityPresetIndex)); OnPropertyChanged(nameof(IsCustomQualityPreset)); OnPropertyChanged(nameof(Current)); Persist(); }
    partial void OnQualityCustomTagsChanged(string value) { OnPropertyChanged(nameof(Current)); Persist(); }

    partial void OnProbabilityWeightsChanged(bool value)
    {
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    partial void OnTrailingSeparatorChanged(bool value)
    {
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    private static bool IsThresholdInRange(double value) =>
        double.IsFinite(value) && value >= 0 && value <= 1;

    private GroupRuleRow? FindRow(string? groupId) =>
        string.IsNullOrEmpty(groupId)
            ? null
            : Groups.FirstOrDefault(row => string.Equals(row.GroupId, groupId, StringComparison.Ordinal));

    private static Dictionary<string, string> BuildDisplayNameMap(IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifestGroups is null)
            return map;
        foreach (var group in manifestGroups)
        {
            if (group is null || string.IsNullOrEmpty(group.Id) || map.ContainsKey(group.Id))
                continue;
            map[group.Id] = string.IsNullOrEmpty(group.DisplayName) ? group.Id : group.DisplayName;
        }
        return map;
    }

    private GroupRuleRow CreateRow(
        string groupId,
        bool enabled,
        GroupSortMode sortMode,
        Dictionary<string, string> displayNames)
    {
        var row = new GroupRuleRow(
            groupId,
            displayNames.TryGetValue(groupId, out var displayName) ? displayName : groupId,
            enabled,
            sortMode);
        row.PropertyChanged += OnRowPropertyChanged;
        return row;
    }

    private void RebuildGroups(IEnumerable<GroupRule> rules)
    {
        foreach (var row in Groups)
            row.PropertyChanged -= OnRowPropertyChanged;
        Groups.Clear();
        var displayNames = BuildDisplayNameMap(_manifestGroups);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (rules is not null)
        {
            foreach (var rule in rules)
            {
                if (rule is null || string.IsNullOrEmpty(rule.GroupId) || !seen.Add(rule.GroupId))
                    continue;
                Groups.Add(CreateRow(rule.GroupId, rule.Enabled, rule.SortMode, displayNames));
            }
        }
        // 补齐 manifest 新增组：按默认逆序追加，全部启用、置信度降序。
        if (_manifestGroups is not null)
        {
            for (int i = _manifestGroups.Count - 1; i >= 0; i--)
            {
                var group = _manifestGroups[i];
                if (group is null || string.IsNullOrEmpty(group.Id) || !seen.Add(group.Id))
                    continue;
                Groups.Add(CreateRow(group.Id, true, GroupSortMode.ConfidenceDescending, displayNames));
            }
        }
    }

    private void ApplyAll(PromptSettings seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        RebuildGroups(seed.GroupRules);

        _groupThresholds.Clear();
        foreach (var entry in seed.Thresholds.GroupThresholds)
        {
            if (string.IsNullOrEmpty(entry.Key) || !IsThresholdInRange(entry.Value))
                continue;
            _groupThresholds[entry.Key] = entry.Value;
        }
        OnPropertyChanged(nameof(GroupThresholds));

        _replacements.Clear();
        foreach (var entry in seed.Transforms.Replacements)
        {
            if (string.IsNullOrEmpty(entry.Key))
                continue;
            _replacements[entry.Key] = entry.Value ?? string.Empty;
        }
        OnPropertyChanged(nameof(Replacements));

        ExcludedTags.Clear();
        foreach (var tag in seed.Transforms.ExcludedTags)
        {
            if (string.IsNullOrEmpty(tag) || ExcludedTags.Contains(tag))
                continue;
            ExcludedTags.Add(tag);
        }

        UseGlobalThreshold = seed.Thresholds.UseGlobalThreshold;
        UnderscoreToSpace = seed.Transforms.UnderscoreToSpace;
        EscapeParentheses = seed.Transforms.EscapeParentheses;
        ProbabilityWeights = seed.Transforms.ProbabilityWeights;
        TrailingSeparator = seed.Output.TrailingSeparator;
        Prefix = seed.Output.Prefix ?? string.Empty;
        Suffix = seed.Output.Suffix ?? string.Empty;
        TagSeparator = seed.Output.TagSeparator;
        GroupSeparator = seed.Output.GroupSeparator;
        QualityPreset = seed.Output.QualityPreset;
        QualityCustomTags = seed.Output.QualityCustomTags ?? string.Empty;
        MaxTags = seed.Output.MaxTags;
        MinWeight = seed.Transforms.MinWeight;
        MaxWeight = seed.Transforms.MaxWeight;
        OnPropertyChanged(nameof(Current));
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GroupRuleRow.Enabled)
            && e.PropertyName != nameof(GroupRuleRow.SortMode))
            return;
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Move 同时上报新旧项，订阅保持不变，直接跳过。
        if (e.Action != NotifyCollectionChangedAction.Move)
        {
            if (e.NewItems is not null)
            {
                foreach (GroupRuleRow row in e.NewItems)
                {
                    row.PropertyChanged -= OnRowPropertyChanged;
                    row.PropertyChanged += OnRowPropertyChanged;
                }
            }
            if (e.OldItems is not null)
            {
                foreach (GroupRuleRow row in e.OldItems)
                    row.PropertyChanged -= OnRowPropertyChanged;
            }
        }
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    private void OnExcludedTagsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Current));
        Persist();
    }

    private void Persist()
    {
        if (_suppressPersist)
            return;
        _store?.Save(Current);
    }
}
