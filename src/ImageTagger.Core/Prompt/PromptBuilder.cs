using System.Globalization;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;

namespace ImageTagger.Core.Prompt;

/// <summary>Prompt 构建结果：最终字符串、标签数与字符数。</summary>
public sealed record BuildResult(string Prompt, int TagCount, int CharCount)
{
    /// <summary>空结果：空输入或无有效标签时返回。</summary>
    public static readonly BuildResult Empty = new(string.Empty, 0, 0);
}

/// <summary>
/// 纯函数 Prompt 构建器（无 UI 依赖），严格按 DESIGN 10.6 的 12 步实现。
/// 相同输入必须产生相同输出；空输入返回空字符串。
/// </summary>
public static class PromptBuilder
{
    private const string RatingGroupId = "rating";
    private const double WeightEpsilon = 1e-12;

    /// <summary>
    /// 构建 Prompt。
    /// </summary>
    /// <param name="catalog">共享标签目录。</param>
    /// <param name="prediction">当前图片推理快照；为空或长度不匹配时返回空。</param>
    /// <param name="selection">用户手动排除；为空表示无排除。</param>
    /// <param name="defaultThreshold">调用方传入的默认阈值（ModelDescriptor.DefaultThreshold 或 UI 全局阈值），范围 0..1。</param>
    /// <param name="settings">单套 Prompt 规则。</param>
    /// <param name="manifestGroups">Model Pack 声明的分组（顺序为标签页显示顺序）；为空时回落到目录分组。</param>
    public static BuildResult Build(
        TagCatalog? catalog,
        PredictionSnapshot? prediction,
        TagSelection? selection,
        double defaultThreshold,
        PromptSettings? settings,
        IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        // 第 1 步：快照有效性，快照过期或不存在则输出空状态。
        if (catalog is null || prediction is null || settings is null)
            return BuildResult.Empty;
        var probabilities = prediction.Probabilities;
        if (probabilities is null || probabilities.Length != catalog.Count)
            return BuildResult.Empty;
        if (!double.IsFinite(defaultThreshold) || defaultThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(defaultThreshold));
        ValidateSettings(settings);

        // 确定分组顺序：按 GroupRules 顺序；为空则用 manifest 逆序。
        var orderedGroups = ResolveOrderedGroups(settings, catalog, manifestGroups);
        if (orderedGroups.Count == 0)
        {
            // 无有效分组时仅返回前后缀（若为空则为空结果）。
            return Finish(settings);
        }

        var forceExcluded = selection?.ForceExcludedIndices;
        var exclusionSet = settings.Transforms.ExcludedTags.Count == 0
            ? null
            : new HashSet<string>(settings.Transforms.ExcludedTags, StringComparer.Ordinal);

        // 按组收集候选（第 2/3/4/5 步），保留概率与有效阈值供后续权重使用。
        var perGroupCandidates = new List<GroupCandidates>(orderedGroups.Count);
        foreach (var group in orderedGroups)
        {
            double effective = settings.Thresholds.UseGlobalThreshold
                ? defaultThreshold
                : settings.Thresholds.Effective(defaultThreshold, group.GroupId);
            if (!double.IsFinite(effective) || effective is < 0 or > 1)
                continue;

            List<Candidate> items;
            if (string.Equals(group.GroupId, RatingGroupId, StringComparison.Ordinal))
            {
                // 第 3 步：分级组取最高概率标签，除非规则禁用分级（禁用组已在排序外）。
                items = CollectTopRating(catalog, probabilities);
            }
            else
            {
                items = CollectThresholdGroup(catalog, probabilities, group.GroupId, effective);
            }

            // 第 4 步：移除用户在标签页取消勾选的项。
            // 第 5 步：应用排除列表（精确匹配原始 tag）。
            if (items.Count != 0 && (forceExcluded is { Count: > 0 } || exclusionSet is not null))
            {
                items.RemoveAll(c =>
                    (forceExcluded is not null && forceExcluded.Contains(c.Index)) ||
                    (exclusionSet is not null && exclusionSet.Contains(c.OriginalName)));
            }

            // 第 7 步：每组按配置排序。
            SortGroup(items, group.SortMode);
            perGroupCandidates.Add(new GroupCandidates(group.GroupId, effective, items));
        }

        // 第 8 步：应用精确替换和下划线转换；第 9 步：规范化首尾空白并去重。
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var perGroupRaw = new List<GroupRawTexts>(perGroupCandidates.Count);
        foreach (var group in perGroupCandidates)
        {
            var texts = new List<WeightedCandidate>(group.Items.Count);
            foreach (var candidate in group.Items)
            {
                string transformed = Transform(candidate.OriginalName, settings.Transforms);
                if (transformed.Length == 0)
                    continue;
                if (!seen.Add(transformed))
                    continue;
                texts.Add(new WeightedCandidate(transformed, candidate.Probability));
            }
            perGroupRaw.Add(new GroupRawTexts(group.GroupId, group.EffectiveThreshold, texts));
        }

        // 第 10 步：应用可选权重。
        var perGroupFinal = new List<GroupTexts>(perGroupRaw.Count);
        foreach (var group in perGroupRaw)
        {
            var finals = new List<string>(group.Texts.Count);
            foreach (var item in group.Texts)
            {
                finals.Add(ApplyWeight(item.Text, item.Probability, group.EffectiveThreshold, settings.Transforms));
            }
            perGroupFinal.Add(new GroupTexts(group.GroupId, group.EffectiveThreshold, finals));
        }

        // 第 11 步：应用最大标签数（按最终组顺序截断）。
        // 第 12 步：用分隔符连接，并添加前缀与后缀。
        return FinishGroups(perGroupFinal, settings);
    }

    /// <summary>使用 ModelDescriptor 的便捷重载：默认阈值与分组均取自描述符。</summary>
    public static BuildResult Build(
        TagCatalog? catalog,
        PredictionSnapshot? prediction,
        TagSelection? selection,
        ModelDescriptor? descriptor,
        PromptSettings? settings)
    {
        if (descriptor is null)
            return BuildResult.Empty;
        return Build(catalog, prediction, selection, descriptor.DefaultThreshold, settings, descriptor.Groups);
    }

    /// <summary>不抛空引用异常的构建；输入无效返回 false，阈值越界等参数错误亦返回 false。</summary>
    public static bool TryBuild(
        TagCatalog? catalog,
        PredictionSnapshot? prediction,
        TagSelection? selection,
        double defaultThreshold,
        PromptSettings? settings,
        IReadOnlyList<GroupDescriptor>? manifestGroups,
        out BuildResult result)
    {
        try
        {
            result = Build(catalog, prediction, selection, defaultThreshold, settings, manifestGroups);
            // 空输入（目录/快照/规则缺失或长度不匹配）视为构建失败。
            if (catalog is null || prediction is null || settings is null)
                return false;
            if (prediction.Probabilities is null || prediction.Probabilities.Length != catalog.Count)
                return false;
            return true;
        }
        catch (ArgumentException)
        {
            result = BuildResult.Empty;
            return false;
        }
    }

    /// <summary>TryBuild 的 ModelDescriptor 重载。</summary>
    public static bool TryBuild(
        TagCatalog? catalog,
        PredictionSnapshot? prediction,
        TagSelection? selection,
        ModelDescriptor? descriptor,
        PromptSettings? settings,
        out BuildResult result)
    {
        if (descriptor is null)
        {
            result = BuildResult.Empty;
            return false;
        }
        return TryBuild(catalog, prediction, selection, descriptor.DefaultThreshold, settings, descriptor.Groups, out result);
    }

    private sealed record OrderedGroup(string GroupId, GroupSortMode SortMode);

    private sealed record Candidate(int Index, string OriginalName, float Probability);

    private sealed record GroupCandidates(string GroupId, double EffectiveThreshold, List<Candidate> Items);

    private sealed record WeightedCandidate(string Text, float Probability);

    private sealed record GroupRawTexts(string GroupId, double EffectiveThreshold, List<WeightedCandidate> Texts);

    private sealed record GroupTexts(string GroupId, double EffectiveThreshold, List<string> Texts);

    private static List<OrderedGroup> ResolveOrderedGroups(
        PromptSettings settings,
        TagCatalog catalog,
        IReadOnlyList<GroupDescriptor>? manifestGroups)
    {
        var result = new List<OrderedGroup>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        if (settings.GroupRules.Count > 0)
        {
            // 按用户配置的分组顺序排列，仅保留启用组。
            foreach (var rule in settings.GroupRules)
            {
                if (rule is null || string.IsNullOrEmpty(rule.GroupId))
                    continue;
                if (!seenIds.Add(rule.GroupId))
                    continue;
                if (rule.Enabled)
                    result.Add(new OrderedGroup(rule.GroupId, rule.SortMode));
            }

            // 补齐 manifest 中新增但规则缺失的分组（默认启用、置信度降序），保持逆序默认。
            if (manifestGroups is not null)
            {
                var mentioned = new HashSet<string>(StringComparer.Ordinal);
                foreach (var rule in settings.GroupRules)
                {
                    if (rule is not null && !string.IsNullOrEmpty(rule.GroupId))
                        mentioned.Add(rule.GroupId);
                }
                for (int i = manifestGroups.Count - 1; i >= 0; i--)
                {
                    var id = manifestGroups[i]?.Id;
                    if (string.IsNullOrEmpty(id) || !mentioned.Add(id) || !seenIds.Add(id))
                        continue;
                    result.Add(new OrderedGroup(id, GroupSortMode.ConfidenceDescending));
                }
            }

            // 补齐目录中存在但两处均未声明的分组（首次出现顺序）。
            AppendMissingCatalogGroups(catalog, result, seenIds);
            return result;
        }

        // 默认顺序 = manifest groups 逆序；manifest 缺失时回落到目录分组。
        if (manifestGroups is { Count: > 0 })
        {
            for (int i = manifestGroups.Count - 1; i >= 0; i--)
            {
                var id = manifestGroups[i]?.Id;
                if (string.IsNullOrEmpty(id) || !seenIds.Add(id))
                    continue;
                result.Add(new OrderedGroup(id, GroupSortMode.ConfidenceDescending));
            }
            AppendMissingCatalogGroups(catalog, result, seenIds);
            return result;
        }

        AppendMissingCatalogGroups(catalog, result, seenIds);
        return result;
    }

    private static void AppendMissingCatalogGroups(
        TagCatalog catalog,
        List<OrderedGroup> result,
        HashSet<string> seenIds)
    {
        foreach (var entry in catalog.Entries)
        {
            if (!seenIds.Add(entry.DisplayGroup))
                continue;
            result.Add(new OrderedGroup(entry.DisplayGroup, GroupSortMode.ConfidenceDescending));
        }
    }

    private static List<Candidate> CollectTopRating(TagCatalog catalog, float[] probabilities)
    {
        int bestIndex = -1;
        float bestProb = float.NegativeInfinity;
        for (int i = 0; i < catalog.Count; i++)
        {
            var entry = catalog[i];
            if (!string.Equals(entry.DisplayGroup, RatingGroupId, StringComparison.Ordinal))
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
        var best = catalog[bestIndex];
        return [new Candidate(bestIndex, best.OriginalName, probabilities[bestIndex])];
    }

    private static List<Candidate> CollectThresholdGroup(
        TagCatalog catalog,
        float[] probabilities,
        string groupId,
        double effectiveThreshold)
    {
        var items = new List<Candidate>();
        for (int i = 0; i < catalog.Count; i++)
        {
            var entry = catalog[i];
            if (!string.Equals(entry.DisplayGroup, groupId, StringComparison.Ordinal))
                continue;
            float prob = probabilities[i];
            // 阈值边界 == 包含；非有限概率直接跳过。
            if (!float.IsFinite(prob) || (double)prob < effectiveThreshold)
                continue;
            items.Add(new Candidate(i, entry.OriginalName, prob));
        }
        return items;
    }

    private static void SortGroup(List<Candidate> items, GroupSortMode sortMode)
    {
        if (items.Count < 2)
            return;
        if (sortMode == GroupSortMode.NameAscending)
        {
            // 按原始标签字母序（序号 Ordinal），相同则按索引升序保证稳定。
            items.Sort(static (a, b) =>
            {
                int name = string.CompareOrdinal(a.OriginalName, b.OriginalName);
                return name != 0 ? name : a.Index.CompareTo(b.Index);
            });
        }
        else
        {
            // 默认置信度降序，概率相同按模型索引升序。
            items.Sort(static (a, b) =>
            {
                int confidence = b.Probability.CompareTo(a.Probability);
                return confidence != 0 ? confidence : a.Index.CompareTo(b.Index);
            });
        }
    }

    private static string Transform(string originalName, TransformRules transforms)
    {
        string current = originalName;
        // 精确替换（原始 tag 精确匹配，不支持正则）。
        if (transforms.Replacements.TryGetValue(current, out var replacement))
            current = replacement ?? string.Empty;
        // 下划线转换。
        if (transforms.UnderscoreToSpace && current.Contains('_')
            && current.Any(char.IsLetterOrDigit)
            && !LooksLikeEmojiOrEmoticon(current))
            current = current.Replace('_', ' ');
        if (transforms.EscapeParentheses)
            current = current.Replace("(", "\\(", StringComparison.Ordinal)
                .Replace(")", "\\)", StringComparison.Ordinal);
        // 规范化首尾空白。
        return current.Trim();
    }

    private static bool ContainsTextualCharacter(string value) =>
        value.Any(char.IsLetterOrDigit);

    private static bool LooksLikeEmojiOrEmoticon(string value)
    {
        if (value.Any(c => char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.OtherSymbol
            or System.Globalization.UnicodeCategory.MathSymbol))
            return true;
        return value.Length <= 8
            && value.Length > 1
            && value.All(c => "xXoO0:;=^vTt_()[]<>-".Contains(c, StringComparison.Ordinal));
    }

    private static string ApplyWeight(string text, float probability, double effectiveThreshold, TransformRules transforms)
    {
        if (!transforms.ProbabilityWeights)
            return text;
        // 概率先限制在 [有效阈值, 1]。
        double clamped = Math.Min(Math.Max((double)probability, effectiveThreshold), 1.0);
        double weight;
        double range = 1.0 - effectiveThreshold;
        if (range <= WeightEpsilon)
        {
            // 有效阈值为 1 时仅满分通过，直接取上限避免除零。
            weight = transforms.MaxWeight;
        }
        else
        {
            weight = transforms.MinWeight
                + ((clamped - effectiveThreshold) / range) * (transforms.MaxWeight - transforms.MinWeight);
        }
        weight = Math.Min(Math.Max(weight, Math.Min(transforms.MinWeight, transforms.MaxWeight)), Math.Max(transforms.MinWeight, transforms.MaxWeight));
        double rounded = Math.Round(weight, 2, MidpointRounding.AwayFromZero);
        // 等于 1.00 时不包裹权重语法。
        if (rounded == 1.0)
            return text;
        return string.Create(CultureInfo.InvariantCulture, $"({text}:{rounded:F2})");
    }

    private static BuildResult Finish(PromptSettings settings)
    {
        string prefix = settings.Output.Prefix ?? string.Empty;
        string suffix = settings.Output.Suffix ?? string.Empty;
        string prompt = prefix + suffix;
        if (prompt.Length == 0)
            return BuildResult.Empty;
        return new BuildResult(prompt, 0, prompt.Length);
    }

    private static BuildResult FinishGroups(
        List<GroupTexts> groups,
        PromptSettings settings)
    {
        // 按组顺序取前 MaxTags 个标签（保持组内排序与组间顺序）。
        int remaining = Math.Max(1, settings.Output.MaxTags);
        var groupStrings = new List<string>(groups.Count);
        int counted = 0;
        foreach (var group in groups)
        {
            if (remaining <= 0 || group.Texts.Count == 0)
                continue;
            int take = Math.Min(group.Texts.Count, remaining);
            var slice = group.Texts.GetRange(0, take);
            remaining -= take;
            counted += take;
            // 组内用 TagSeparator 连接。
            groupStrings.Add(string.Join(settings.Output.TagSeparator, slice));
        }

        // 非空组之间用 GroupSeparator 连接。
        string joined = string.Join(settings.Output.GroupSeparator, groupStrings);
        var quality = settings.Output.QualityPreset switch
        {
            QualityTagPreset.StableDiffusion => "masterpiece, best quality",
            QualityTagPreset.Sdxl => "masterpiece, best quality",
            QualityTagPreset.Pony => "score_9, score_8_up, score_7_up, score_6_up",
            QualityTagPreset.Illustrious => "masterpiece, best quality, amazing quality, very aesthetic",
            QualityTagPreset.NoobAI => "masterpiece, best quality, newest, absurdres, highres",
            QualityTagPreset.Anima => "masterpiece, best quality, score_7",
            QualityTagPreset.Custom => settings.Output.QualityCustomTags?.Trim() ?? string.Empty,
            _ => string.Empty,
        };
        if (quality.Length > 0)
            joined = joined.Length == 0 ? quality : quality + settings.Output.GroupSeparator + joined;
        if (settings.Output.TrailingSeparator && joined.Length > 0)
            joined += settings.Output.TagSeparator;

        // 前缀/后缀非空时直接拼接，不额外插入分隔符。
        string prefix = settings.Output.Prefix ?? string.Empty;
        string suffix = settings.Output.Suffix ?? string.Empty;
        string prompt = prefix + joined + suffix;
        if (prompt.Length == 0)
            return BuildResult.Empty;
        // 空标签但有前后缀时标签数为实际标签数（可能为 0），字符数含前后缀。
        return new BuildResult(prompt, counted, prompt.Length);
    }

    private static void ValidateSettings(PromptSettings settings)
    {
        if (settings.Output.TagSeparator is null || settings.Output.TagSeparator.Length == 0)
            throw new ArgumentException("标签分隔符不允许为空。", nameof(settings));
        if (settings.Output.GroupSeparator is null || settings.Output.GroupSeparator.Length == 0)
            throw new ArgumentException("分组分隔符不允许为空。", nameof(settings));
        if (settings.Output.MaxTags is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(settings), "最大标签数范围为 1..500。");
        foreach (var pair in settings.Thresholds.GroupThresholds)
        {
            if (!double.IsFinite(pair.Value) || pair.Value is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(settings), $"分组阈值 '{pair.Key}' 范围为 0..1。");
        }
        if (!double.IsFinite(settings.Transforms.MinWeight) || settings.Transforms.MinWeight is < 0.5 or > 5)
            throw new ArgumentOutOfRangeException(nameof(settings), "权重下限范围为 0.5..5。");
        if (!double.IsFinite(settings.Transforms.MaxWeight) || settings.Transforms.MaxWeight is < 0.5 or > 5)
            throw new ArgumentOutOfRangeException(nameof(settings), "权重上限范围为 0.5..5。");
        if (settings.Transforms.MinWeight > settings.Transforms.MaxWeight)
            throw new ArgumentException("权重下限不得大于上限。", nameof(settings));
        foreach (var rule in settings.GroupRules)
        {
            if (rule is null || string.IsNullOrEmpty(rule.GroupId))
                throw new ArgumentException("分组规则的 GroupId 不允许为空。", nameof(settings));
            if (!Enum.IsDefined(rule.SortMode))
                throw new ArgumentException($"未知组内排序 '{rule.SortMode}'。", nameof(settings));
        }
    }
}
