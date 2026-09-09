namespace ImageTagger.Core.Domain;

/// <summary>In-group sort order for prompt building (design 10.3); default is confidence descending.</summary>
public enum GroupSortMode
{
    ConfidenceDescending,
    NameAscending,
}

public enum QualityTagPreset
{
    None,
    StableDiffusion,
    Sdxl,
    Pony,
    Illustrious,
    NoobAI,
    Anima,
    Custom,
}

/// <summary>Per-group prompt rule driven by the Model Pack manifest's declared groups.</summary>
public sealed record GroupRule
{
    public required string GroupId { get; init; }

    public bool Enabled { get; init; } = true;

    public GroupSortMode SortMode { get; init; } = GroupSortMode.ConfidenceDescending;
}

/// <summary>Threshold configuration: one global threshold with optional per-group overrides.</summary>
public sealed record ThresholdRules
{
    public bool UseGlobalThreshold { get; init; } = true;

    /// <summary>Per-group overrides keyed by group id; used only when <see cref="UseGlobalThreshold"/> is false.</summary>
    public IReadOnlyDictionary<string, double> GroupThresholds { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Effective threshold for a group given the model default.</summary>
    public double Effective(double defaultThreshold, string groupId) =>
        UseGlobalThreshold || !GroupThresholds.TryGetValue(groupId, out var value)
            ? defaultThreshold
            : value;
}

/// <summary>Tag text transforms applied during prompt building (design 10.4).</summary>
public sealed record TransformRules
{
    public bool UnderscoreToSpace { get; init; } = true;

    public bool EscapeParentheses { get; init; } = true;

    /// <summary>Exact original tags, one per line semantics; never a search box.</summary>
    public IReadOnlyList<string> ExcludedTags { get; init; } = [];

    /// <summary>Exact-match replacements: original tag -> output text. No regex.</summary>
    public IReadOnlyDictionary<string, string> Replacements { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public bool ProbabilityWeights { get; init; }

    public double MinWeight { get; init; } = 1.00;

    public double MaxWeight { get; init; } = 1.30;
}

/// <summary>Output formatting rules (design 10.5).</summary>
public sealed record OutputRules
{
    /// <summary>Tag separator; empty string is not allowed, use " " or ", " or "\n".</summary>
    public string TagSeparator { get; init; } = ", ";

    public string GroupSeparator { get; init; } = ", ";

    public string Prefix { get; init; } = "";

    public string Suffix { get; init; } = "";

    /// <summary>Truncation applied in final group order; range 1-500, default 100.</summary>
    public int MaxTags { get; init; } = 100;

    public bool TrailingSeparator { get; init; }

    public QualityTagPreset QualityPreset { get; init; } = QualityTagPreset.None;

    public string QualityCustomTags { get; init; } = string.Empty;
}

/// <summary>
/// The single prompt rule set persisted as prompt-settings.json (design 10.2).
/// Deduplication is always on and therefore not configurable.
/// </summary>
public sealed record PromptSettings
{
    public IReadOnlyList<GroupRule> GroupRules { get; init; } = [];

    public ThresholdRules Thresholds { get; init; } = new();

    public TransformRules Transforms { get; init; } = new();

    public OutputRules Output { get; init; } = new();
}
