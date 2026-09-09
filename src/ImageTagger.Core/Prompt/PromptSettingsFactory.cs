using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;

namespace ImageTagger.Core.Prompt;

/// <summary>
/// Prompt 默认规则工厂：默认分组顺序为 manifest Groups 逆序，全部启用、置信度降序。
/// </summary>
public static class PromptSettingsFactory
{
    /// <summary>
    /// 按 ModelDescriptor 创建默认规则（DESIGN 10.3：主题标签在前、分级最后）。
    /// </summary>
    public static PromptSettings CreateDefault(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CreateDefault(descriptor.Groups);
    }

    /// <summary>按分组声明创建默认规则；分组为空时返回空规则的默认设置。</summary>
    public static PromptSettings CreateDefault(IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        var rules = new List<GroupRule>();
        if (manifestGroups is not null)
        {
            // 默认顺序为 manifest 数组的逆序，全部启用、置信度降序。
            for (int i = manifestGroups.Count - 1; i >= 0; i--)
            {
                var group = manifestGroups[i];
                if (group is null || string.IsNullOrEmpty(group.Id))
                    continue;
                rules.Add(new GroupRule
                {
                    GroupId = group.Id,
                    Enabled = true,
                    SortMode = GroupSortMode.ConfidenceDescending,
                });
            }
        }

        // 阈值默认全局；变换默认下划线开；输出默认 ", "/100。
        return new PromptSettings
        {
            GroupRules = rules,
            Thresholds = new ThresholdRules
            {
                UseGlobalThreshold = true,
                GroupThresholds = new Dictionary<string, double>(StringComparer.Ordinal),
            },
            Transforms = new TransformRules
            {
                UnderscoreToSpace = true,
                ExcludedTags = [],
                Replacements = new Dictionary<string, string>(StringComparer.Ordinal),
                ProbabilityWeights = false,
                MinWeight = 1.00,
                MaxWeight = 1.30,
            },
            Output = new OutputRules
            {
                TagSeparator = ", ",
                GroupSeparator = ", ",
                Prefix = string.Empty,
                Suffix = string.Empty,
                MaxTags = 100,
                TrailingSeparator = false,
            },
        };
    }

    /// <summary>无 manifest 时的默认规则（空分组顺序，其余与默认一致）。</summary>
    public static PromptSettings CreateDefault() => CreateDefault((IReadOnlyList<GroupDescriptor>?)null);
}
