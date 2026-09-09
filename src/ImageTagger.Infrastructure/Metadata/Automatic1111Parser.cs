using System.Text;
using ImageTagger.Core.Domain;

namespace ImageTagger.Infrastructure.Metadata;

/// <summary>
/// Parses AUTOMATIC1111 / Forge "parameters" text into <see cref="GenerationInfo"/>.
/// The positive prompt, negative prompt and every unrecognized tail field keep
/// their original text; only documented tail keys are normalized into parameters.
/// </summary>
public static class Automatic1111Parser
{
    private const string NegativePromptMarker = "Negative prompt:";

    /// <summary>
    /// Parses one parameters block. Returns null only for empty input.
    /// A text containing a Forge marker yields <see cref="GenerationSource.Forge"/>,
    /// otherwise <see cref="GenerationSource.Automatic1111"/>.
    /// </summary>
    public static GenerationInfo? Parse(string? parametersText)
    {
        if (string.IsNullOrWhiteSpace(parametersText))
        {
            return null;
        }

        var source = parametersText.Contains("Forge", StringComparison.OrdinalIgnoreCase)
            ? GenerationSource.Forge
            : GenerationSource.Automatic1111;

        var (positive, negative, tail) = SplitSections(parametersText);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawEntries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["parameters"] = parametersText,
        };
        var resources = new List<GenerationResource>();
        var warnings = new List<string>();
        string? softwareVersion = null;
        string? checkpointName = null;
        string? checkpointHash = null;
        string? vaeName = null;

        if (string.IsNullOrEmpty(tail))
        {
            warnings.Add("未找到尾部参数行，仅保留提示词。");
        }
        else
        {
            foreach (var segment in SplitTopLevel(tail))
            {
                var colon = segment.IndexOf(':');
                if (colon <= 0)
                {
                    rawEntries[segment] = string.Empty;
                    continue;
                }

                var rawKey = segment.Substring(0, colon).Trim();
                var rawValue = segment.Substring(colon + 1).Trim();
                if (rawKey.Length == 0)
                {
                    continue;
                }

                MapTailField(rawKey, rawValue, parameters, rawEntries, warnings,
                    ref softwareVersion, ref checkpointName, ref checkpointHash, ref vaeName);
            }
        }

        if (checkpointName is not null || checkpointHash is not null)
        {
            resources.Add(new GenerationResource(
                GenerationResourceKind.Checkpoint,
                checkpointName ?? checkpointHash!,
                checkpointHash,
                null));
        }

        if (vaeName is not null)
        {
            resources.Add(new GenerationResource(GenerationResourceKind.Vae, vaeName, null, null));
        }

        return new GenerationInfo
        {
            Source = source,
            SoftwareVersion = softwareVersion,
            PositivePrompt = string.IsNullOrWhiteSpace(positive) ? null : positive,
            NegativePrompt = string.IsNullOrWhiteSpace(negative) ? null : negative,
            Parameters = parameters,
            Resources = resources,
            RawEntries = rawEntries,
            Warnings = warnings,
        };
    }

    private static (string Positive, string? Negative, string Tail) SplitSections(string text)
    {
        var negativeIndex = text.IndexOf(NegativePromptMarker, StringComparison.Ordinal);
        if (negativeIndex >= 0)
        {
            var positive = text.Substring(0, negativeIndex).Trim();
            var remainder = text.Substring(negativeIndex + NegativePromptMarker.Length);
            var (negative, tail) = SplitTail(remainder);
            return (positive, negative, tail);
        }

        var (onlyPositive, onlyTail) = SplitTail(text);
        return (onlyPositive, null, onlyTail);
    }

    private static (string Head, string Tail) SplitTail(string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var tailStart = -1;
        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (lines[i].TrimStart().StartsWith("Steps:", StringComparison.OrdinalIgnoreCase))
            {
                tailStart = i;
                break;
            }
        }

        if (tailStart < 0)
        {
            return (text.Trim(), string.Empty);
        }

        return (
            string.Join('\n', lines.Take(tailStart)).Trim(),
            string.Join(' ', lines.Skip(tailStart)).Trim());
    }

    /// <summary>
    /// Splits a tail parameter line on commas that are not wrapped in quotes or
    /// brackets, so values such as quoted phrases or parenthesized weights stay intact.
    /// </summary>
    internal static List<string> SplitTopLevel(string text)
    {
        var parts = new List<string>();
        var current = new StringBuilder(text.Length);
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        foreach (var c in text)
        {
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
                current.Append(c);
                continue;
            }

            if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
                current.Append(c);
                continue;
            }

            if (!inSingleQuote && !inDoubleQuote)
            {
                switch (c)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        parenDepth = Math.Max(0, parenDepth - 1);
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        bracketDepth = Math.Max(0, bracketDepth - 1);
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        braceDepth = Math.Max(0, braceDepth - 1);
                        break;
                    case ',' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                        parts.Add(current.ToString().Trim());
                        current.Clear();
                        continue;
                }
            }

            current.Append(c);
        }

        parts.Add(current.ToString().Trim());
        return parts.Where(part => part.Length > 0).ToList();
    }

    private static void MapTailField(
        string rawKey,
        string rawValue,
        Dictionary<string, string> parameters,
        Dictionary<string, string> rawEntries,
        List<string> warnings,
        ref string? softwareVersion,
        ref string? checkpointName,
        ref string? checkpointHash,
        ref string? vaeName)
    {
        var key = rawKey.Trim().ToLowerInvariant();
        switch (key)
        {
            case "steps":
                parameters["steps"] = rawValue;
                return;
            case "sampler":
                parameters["sampler"] = rawValue;
                return;
            case "scheduler":
            case "schedule type":
            case "schedule":
                parameters["scheduler"] = rawValue;
                return;
            case "cfg scale":
            case "cfg":
                parameters["cfg"] = rawValue;
                return;
            case "seed":
                parameters["seed"] = rawValue;
                return;
            case "size":
                parameters["size"] = rawValue;
                return;
            case "model hash":
                parameters["model_hash"] = rawValue;
                checkpointHash = rawValue;
                return;
            case "model":
                parameters["model"] = rawValue;
                checkpointName = rawValue;
                return;
            case "vae":
                parameters["vae"] = rawValue;
                vaeName = rawValue;
                return;
            case "vae hash":
                parameters["vae_hash"] = rawValue;
                vaeName ??= rawValue;
                return;
            case "clip skip":
            case "clipskip":
                parameters["clip_skip"] = rawValue;
                return;
            case "denoising strength":
                parameters["denoising"] = rawValue;
                return;
            case "version":
                softwareVersion = rawValue;
                parameters["version"] = rawValue;
                return;
            case "face restoration":
                parameters["face_restoration"] = rawValue;
                return;
            case "tiling":
                parameters["tiling"] = rawValue;
                return;
        }

        if (key.StartsWith("hires", StringComparison.Ordinal))
        {
            parameters["hires_" + NormalizeSuffix(rawKey, "hires")] = rawValue;
            return;
        }

        if (key.StartsWith("refiner", StringComparison.Ordinal))
        {
            var suffix = NormalizeSuffix(rawKey, "refiner");
            parameters[suffix.Length == 0 ? "refiner" : "refiner_" + suffix] = rawValue;
            return;
        }

        warnings.Add($"未识别尾部字段“{rawKey}”，已保留原文。");
        rawEntries[rawKey] = rawValue;
    }

    private static string NormalizeSuffix(string rawKey, string prefix)
    {
        var suffix = rawKey.Trim().Substring(prefix.Length).Trim();
        var builder = new StringBuilder(suffix.Length);
        foreach (var c in suffix.ToLowerInvariant())
        {
            builder.Append(c is ' ' or '-' or '/' ? '_' : c);
        }

        return builder.ToString().Trim('_');
    }
}
