using System.Text.Json;
using ImageTagger.Core.Domain;

namespace ImageTagger.Infrastructure.Metadata;

/// <summary>
/// Parses NovelAI comment JSON together with Software/Source markers.
/// Scalar comment fields other than the prompts become normalized parameters;
/// complex values stay in raw entries. Resources are always empty for NovelAI.
/// Unrecognized input yields an <see cref="GenerationSource.Unknown"/> fallback
/// that still preserves the discovered keys for upper-layer assembly.
/// </summary>
public static class NovelAiParser
{
    /// <summary>
    /// Always returns a <see cref="GenerationInfo"/>; check
    /// <see cref="GenerationInfo.Source"/> to tell NovelAI from the fallback.
    /// </summary>
    public static GenerationInfo Parse(string? commentJson, string? software = null, string? source = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        string? positive = null;
        string? negative = null;

        var softwareHit = ContainsNovelAi(software) || ContainsNovelAi(source);
        var hasPromptKeys = false;

        if (!string.IsNullOrWhiteSpace(commentJson))
        {
            rawEntries["comment"] = Truncate(commentJson);
            try
            {
                using var document = JsonDocument.Parse(
                    commentJson,
                    new JsonDocumentOptions { MaxDepth = GenerationMetadataReader.MaxJsonDepth });
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    (positive, negative, hasPromptKeys) = ExtractComment(
                        document.RootElement, parameters, rawEntries);
                }
                else
                {
                    warnings.Add("Comment不是JSON对象，已保留原文。");
                }
            }
            catch (JsonException)
            {
                warnings.Add("Comment JSON解析失败或深度超限，已保留原文。");
            }
        }

        if (software is not null)
        {
            rawEntries["software"] = Truncate(software);
        }

        if (source is not null)
        {
            rawEntries["source"] = Truncate(source);
        }

        var recognized = softwareHit || hasPromptKeys;
        if (!recognized)
        {
            warnings.Add("未识别为NovelAI，已保留原始键值。");
            return new GenerationInfo
            {
                Source = GenerationSource.Unknown,
                Parameters = parameters,
                RawEntries = rawEntries,
                Warnings = warnings,
            };
        }

        return new GenerationInfo
        {
            Source = GenerationSource.NovelAi,
            SoftwareVersion = software,
            PositivePrompt = positive,
            NegativePrompt = negative,
            Parameters = parameters,
            Resources = [],
            RawEntries = rawEntries,
            Warnings = warnings,
        };
    }

    private static (string? Positive, string? Negative, bool HasPromptKeys) ExtractComment(
        JsonElement root,
        Dictionary<string, string> parameters,
        Dictionary<string, string> rawEntries)
    {
        string? positive = null;
        string? negative = null;
        var hasPromptKeys = false;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "prompt", StringComparison.OrdinalIgnoreCase))
            {
                positive = CoerceString(property.Value);
                hasPromptKeys = true;
                continue;
            }

            if (string.Equals(property.Name, "uc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "undesired_content", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "negative_prompt", StringComparison.OrdinalIgnoreCase))
            {
                negative ??= CoerceString(property.Value);
                hasPromptKeys = true;
                continue;
            }

            var scalar = CoerceString(property.Value);
            if (scalar is not null)
            {
                parameters[property.Name.Trim().ToLowerInvariant()] = scalar;
                continue;
            }

            rawEntries[$"comment:{property.Name}"] = Truncate(property.Value.GetRawText());
        }

        return (positive, negative, hasPromptKeys);
    }

    private static string? CoerceString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static bool ContainsNovelAi(string? value) =>
        value is not null && value.Contains("NovelAI", StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string value) => value.Length <= GenerationMetadataReader.MaxFieldLength
        ? value
        : value.Substring(0, GenerationMetadataReader.MaxFieldLength);
}
