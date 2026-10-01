using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Prompt;
using Xunit;

namespace ImageTagger.Tests.Workflows.F;

/// <summary>
/// 分组阈值解析优先级：全局模式使用全局值；按分组模式依次使用用户覆盖、模型包推荐值、全局值。
/// 无推荐值的旧模型包与首版行为完全一致。
/// </summary>
[Trait("Category", "Unit")]
public sealed class GroupThresholdResolverTests
{
    private static readonly GroupDescriptor PackGroup =
        new("general", "General", "通用", DefaultThreshold: 0.2);

    private static readonly GroupDescriptor PlainGroup =
        new("character", "Character", "角色");

    [Fact]
    public void Null_rules_and_global_mode_use_the_global_threshold()
    {
        Assert.Equal(0.6, GroupThresholdResolver.Resolve(null, PackGroup, 0.6));

        var global = new ThresholdRules { UseGlobalThreshold = true };
        Assert.Equal(0.6, GroupThresholdResolver.Resolve(global, PackGroup, 0.6));
    }

    [Fact]
    public void Group_mode_prefers_user_override_then_pack_default_then_global()
    {
        var withOverride = new ThresholdRules
        {
            UseGlobalThreshold = false,
            GroupThresholds = new Dictionary<string, double>(StringComparer.Ordinal) { ["general"] = 0.45 },
        };
        Assert.Equal(0.45, GroupThresholdResolver.Resolve(withOverride, PackGroup, 0.6));

        var packDefaultsOnly = new ThresholdRules { UseGlobalThreshold = false };
        Assert.Equal(0.2, GroupThresholdResolver.Resolve(packDefaultsOnly, PackGroup, 0.6));
        Assert.Equal(0.6, GroupThresholdResolver.Resolve(packDefaultsOnly, PlainGroup, 0.6));
        Assert.Equal(0.6, GroupThresholdResolver.Resolve(packDefaultsOnly, null, 0.6));
    }
}
