using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;

namespace ImageTagger.Core.Prompt;

/// <summary>
/// 分组阈值的唯一解析规则（通用，不依赖具体模型）：
/// 全局阈值模式下始终取全局值；按分组模式下依次取用户覆盖、模型包推荐值、全局值。
/// 旧模型包（无分组推荐值）在两种模式下都只受全局值影响，行为与首版一致。
/// </summary>
public static class GroupThresholdResolver
{
    /// <summary>解析指定分组的有效阈值；分组为空（目录回落分组）时退回全局值。</summary>
    public static double Resolve(ThresholdRules? rules, GroupDescriptor? group, double globalThreshold)
    {
        if (rules is null || rules.UseGlobalThreshold)
            return globalThreshold;
        if (group is not null && rules.GroupThresholds.TryGetValue(group.Id, out var userValue))
            return userValue;
        if (group?.DefaultThreshold is { } packDefault)
            return packDefault;
        return globalThreshold;
    }
}
