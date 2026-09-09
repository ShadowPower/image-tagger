using System.Text.Json;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Metadata;

/// <summary>
/// Implements <see cref="IGenerationInfoParser"/> as extract-all, identify by
/// format, normalize, then keep the original text. Multiple coexisting tool
/// payloads in one file are merged by source: the first non-empty prompt wins
/// while parameters, raw entries, warnings and resources are all combined.
/// </summary>
public sealed class GenerationInfoParser : IGenerationInfoParser
{
    private readonly GenerationMetadataReader _reader;

    public GenerationInfoParser()
        : this(new GenerationMetadataReader())
    {
    }

    public GenerationInfoParser(GenerationMetadataReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>Returns null when no metadata exists or the file cannot be read.</summary>
    public async Task<GenerationInfo?> ParseAsync(ImageDocument image, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        IReadOnlyDictionary<string, string> raw;
        try
        {
            raw = await _reader.ReadRawAsync(image.CanonicalPath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }

        return ParseRaw(raw);
    }

    /// <summary>
    /// Deterministic, file-free merge entry point used by unit tests to cover
    /// multi-source payloads, corrupt fields, truncation and JSON depth limits.
    /// Returns null only when no entries exist; unrecognized keys yield an
    /// <see cref="GenerationSource.Unknown"/> fallback preserving the keys.
    /// </summary>
    public static GenerationInfo? ParseRaw(IReadOnlyDictionary<string, string>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        var warnings = new List<string>();
        var entries = new List<KeyValuePair<string, string>>(raw.Count);
        foreach (var entry in raw)
        {
            if (entry.Key.StartsWith("Warning/", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(entry.Value))
                {
                    warnings.Add(entry.Value);
                }

                continue;
            }

            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            return null;
        }

        entries.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));

        var jsonFailed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (!LooksLikeJson(entry.Value))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(
                    entry.Value,
                    new JsonDocumentOptions { MaxDepth = GenerationMetadataReader.MaxJsonDepth });
            }
            catch (JsonException)
            {
                jsonFailed.Add(entry.Key);
                warnings.Add($"JSON解析失败或深度超限（键“{entry.Key}”），已保留原文。");
            }
        }

        var a1111Texts = entries
            .Where(entry => IsTag(entry.Key, "parameters")
                || entry.Value.Contains("Negative prompt:", StringComparison.Ordinal)
                || entry.Value.Contains("Steps:", StringComparison.Ordinal))
            .ToList();
        var promptJsons = entries
            .Where(entry => !jsonFailed.Contains(entry.Key)
                && (IsTag(entry.Key, "prompt") || entry.Value.Contains("class_type", StringComparison.Ordinal)))
            .ToList();
        var workflowJsons = entries
            .Where(entry => !jsonFailed.Contains(entry.Key)
                && (IsTag(entry.Key, "workflow") || entry.Value.Contains("\"nodes\"", StringComparison.Ordinal)))
            .ToList();
        var commentJsons = entries
            .Where(entry => IsTag(entry.Key, "comment")
                || IsTag(entry.Key, "imagedescription")
                || IsTag(entry.Key, "description")
                || IsTag(entry.Key, "usercomment")
                || IsTag(entry.Key, "xpcomment"))
            .ToList();
        var software = entries.FirstOrDefault(entry => IsTag(entry.Key, "software")).Value;
        var source = entries.FirstOrDefault(entry => IsTag(entry.Key, "source")).Value;

        var segments = new List<GenerationInfo>();
        foreach (var candidate in a1111Texts)
        {
            var parsed = Automatic1111Parser.Parse(candidate.Value);
            if (parsed is not null)
            {
                segments.Add(parsed);
            }
        }

        var firstPrompt = promptJsons.Count > 0 ? promptJsons[0].Value : null;
        var firstWorkflow = workflowJsons.Count > 0 ? workflowJsons[0].Value : null;
        if (firstPrompt is not null || firstWorkflow is not null)
        {
            var combined = ComfyUiParser.Parse(firstPrompt, firstWorkflow);
            if (combined is not null)
            {
                segments.Add(combined);
            }
            else
            {
                foreach (var candidate in promptJsons.Skip(1))
                {
                    var parsed = ComfyUiParser.Parse(candidate.Value, null);
                    if (parsed is not null)
                    {
                        segments.Add(parsed);
                    }
                }

                foreach (var candidate in workflowJsons.Skip(1))
                {
                    var parsed = ComfyUiParser.Parse(null, candidate.Value);
                    if (parsed is not null)
                    {
                        segments.Add(parsed);
                    }
                }
            }
        }

        GenerationInfo? novelFallback = null;
        var firstComment = commentJsons.Count > 0 ? commentJsons[0].Value : null;
        if (firstComment is not null || software is not null || source is not null)
        {
            var parsed = NovelAiParser.Parse(firstComment, software, source);
            if (parsed.Source == GenerationSource.NovelAi)
            {
                segments.Add(parsed);
            }
            else
            {
                novelFallback = parsed;
            }
        }

        GenerationInfo merged = segments.Count == 0
            ? BuildUnknownFallback(entries, warnings, novelFallback)
            : MergeSegments(entries, warnings, segments);

        return EnforceLimits(merged);
    }

    private static GenerationInfo BuildUnknownFallback(
        List<KeyValuePair<string, string>> entries,
        List<string> warnings,
        GenerationInfo? novelFallback)
    {
        if (novelFallback is not null)
        {
            warnings.AddRange(novelFallback.Warnings);
        }

        warnings.Add("未发现支持的AI生成信息，已列出发现的元数据键。");
        var rawEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            rawEntries[entry.Key] = entry.Value;
        }

        return new GenerationInfo
        {
            Source = GenerationSource.Unknown,
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
            Resources = [],
            RawEntries = rawEntries,
            Warnings = warnings,
        };
    }

    private static GenerationInfo MergeSegments(
        List<KeyValuePair<string, string>> entries,
        List<string> warnings,
        List<GenerationInfo> segments)
    {
        foreach (var segment in segments)
        {
            warnings.AddRange(segment.Warnings);
        }

        if (segments.Select(segment => segment.Source).Distinct().Count() > 1)
        {
            warnings.Add("发现多套来源信息，已合并展示。");
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            foreach (var entry in segment.Parameters)
            {
                parameters.TryAdd(entry.Key, entry.Value);
            }
        }

        var rawEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            foreach (var entry in segment.RawEntries)
            {
                rawEntries.TryAdd(entry.Key, entry.Value);
            }
        }

        foreach (var entry in entries)
        {
            rawEntries.TryAdd(entry.Key, entry.Value);
        }

        return new GenerationInfo
        {
            Source = segments[0].Source,
            SoftwareVersion = segments.Select(segment => segment.SoftwareVersion).FirstOrDefault(version => !string.IsNullOrEmpty(version)),
            PositivePrompt = segments.Select(segment => segment.PositivePrompt).FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt)),
            NegativePrompt = segments.Select(segment => segment.NegativePrompt).FirstOrDefault(prompt => !string.IsNullOrWhiteSpace(prompt)),
            Parameters = parameters,
            // 同一份文本可能被多个读取器以上不同键上报（如 MetadataExtractor 与
            // ImageSharp 同时报出 parameters），各解析器会产出完全相同的资源条目；
            // 此处按值去重，不同来源的真实差异条目仍保留展示。
            Resources = segments.SelectMany(segment => segment.Resources).Distinct().ToList(),
            RawEntries = rawEntries,
            Warnings = warnings,
        };
    }

    private static GenerationInfo EnforceLimits(GenerationInfo info)
    {
        var warnings = new List<string>(info.Warnings);
        string? TruncateField(string key, string? value)
        {
            if (value is null || value.Length <= GenerationMetadataReader.MaxFieldLength)
            {
                return value;
            }

            warnings.Add($"字段“{key}”超过2MiB，已截断展示。");
            return value.Substring(0, GenerationMetadataReader.MaxFieldLength);
        }

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in info.Parameters)
        {
            parameters[entry.Key] = TruncateField($"参数{entry.Key}", entry.Value)!;
        }

        var rawEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in info.RawEntries.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            rawEntries[entry.Key] = TruncateField(entry.Key, entry.Value)!;
        }

        long total = rawEntries.Values.Sum(value => (long)value.Length);
        if (total > GenerationMetadataReader.MaxTotalLength)
        {
            var ordered = rawEntries.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList();
            long kept = 0;
            var keptCount = 0;
            foreach (var key in ordered)
            {
                var length = rawEntries[key].Length;
                if (kept + length > GenerationMetadataReader.MaxTotalLength)
                {
                    rawEntries.Remove(key);
                    continue;
                }

                kept += length;
                keptCount++;
            }

            warnings.Add($"原始文本总和超过8MiB，已截断保留前{keptCount}个字段。");
        }

        return info with
        {
            PositivePrompt = TruncateField("正向提示词", info.PositivePrompt),
            NegativePrompt = TruncateField("负向提示词", info.NegativePrompt),
            Parameters = parameters,
            RawEntries = rawEntries,
            Warnings = warnings,
        };
    }

    private static bool LooksLikeJson(string value)
    {
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return c == '{' || c == '[';
        }

        return false;
    }

    /// <summary>
    /// Matches the tag portion of a "directory/tag" key (ImageSharp supplements
    /// use a "directory/kind:name" shape), ignoring case, spaces and underscores.
    /// </summary>
    internal static bool IsTag(string key, string tag)
    {
        var slash = key.LastIndexOf('/');
        var tagPart = slash >= 0 ? key.Substring(slash + 1) : key;
        var colon = tagPart.LastIndexOf(':');
        if (colon >= 0)
        {
            tagPart = tagPart.Substring(colon + 1);
        }

        return Normalize(tagPart).Equals(Normalize(tag), StringComparison.Ordinal);
    }

    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (c != ' ' && c != '_' && c != '-')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
