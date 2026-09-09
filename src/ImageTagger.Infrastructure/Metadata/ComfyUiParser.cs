using System.Text.Json;
using ImageTagger.Core.Domain;

namespace ImageTagger.Infrastructure.Metadata;

/// <summary>
/// Extracts verifiable ComfyUI prompt/workflow node values with
/// <see cref="System.Text.Json"/>. Node values are only read as plain text;
/// paths, scripts and expressions are never executed or resolved. Complex
/// workflows report the confirmable values plus a warning instead of guessing
/// the final composition order.
/// </summary>
public static class ComfyUiParser
{
    /// <summary>
    /// Parses ComfyUI prompt JSON, workflow JSON, or both. Returns null when no
    /// recognizable node is found or when both inputs are empty.
    /// </summary>
    public static GenerationInfo? Parse(string? promptJson, string? workflowJson = null)
    {
        if (string.IsNullOrWhiteSpace(promptJson) && string.IsNullOrWhiteSpace(workflowJson))
        {
            return null;
        }

        var options = new JsonDocumentOptions { MaxDepth = GenerationMetadataReader.MaxJsonDepth };
        var warnings = new List<string>();
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var resources = new List<GenerationResource>();
        var texts = new List<(string NodeId, string Text)>();
        var checkpointNames = new List<string>();
        var vaeNames = new List<string>();
        var loras = new List<(string Name, string? Weight)>();
        var samplers = new List<Dictionary<string, string>>();
        var sizes = new List<(string Size, string? Batch)>();
        var nodeCount = 0;
        var unrecognizedCount = 0;

        if (!string.IsNullOrWhiteSpace(promptJson))
        {
            rawEntries["prompt"] = Truncate(promptJson);
            try
            {
                using var document = JsonDocument.Parse(promptJson, options);
                ExtractFromPromptFormat(document.RootElement, texts, checkpointNames, vaeNames, loras, samplers, sizes, ref nodeCount, ref unrecognizedCount, warnings);
            }
            catch (JsonException)
            {
                warnings.Add("prompt JSON解析失败或深度超限，已保留原文。");
            }
        }

        if (!string.IsNullOrWhiteSpace(workflowJson))
        {
            rawEntries["workflow"] = Truncate(workflowJson);
            try
            {
                using var document = JsonDocument.Parse(workflowJson, options);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("nodes", out var nodes)
                    && nodes.ValueKind == JsonValueKind.Array)
                {
                    ExtractFromWorkflowFormat(nodes, texts, checkpointNames, vaeNames, loras, sizes, ref nodeCount, ref unrecognizedCount, warnings);
                }
                else
                {
                    ExtractFromPromptFormat(document.RootElement, texts, checkpointNames, vaeNames, loras, samplers, sizes, ref nodeCount, ref unrecognizedCount, warnings);
                }
            }
            catch (JsonException)
            {
                warnings.Add("workflow JSON解析失败或深度超限，已保留原文。");
            }
        }

        if (texts.Count == 0 && checkpointNames.Count == 0 && vaeNames.Count == 0
            && loras.Count == 0 && samplers.Count == 0 && sizes.Count == 0)
        {
            return null;
        }

        texts.Sort((left, right) => CompareNodeIds(left.NodeId, right.NodeId));
        string? positive = texts.Count > 0 ? texts[0].Text : null;
        string? negative = texts.Count > 1 ? texts[1].Text : null;
        for (var i = 2; i < texts.Count; i++)
        {
            rawEntries[$"extra_prompt_{i - 1}"] = Truncate(texts[i].Text);
        }

        if (checkpointNames.Count > 0)
        {
            parameters["model"] = checkpointNames[0];
            foreach (var name in checkpointNames)
            {
                resources.Add(new GenerationResource(GenerationResourceKind.Checkpoint, name, null, null));
            }
        }

        if (vaeNames.Count > 0)
        {
            parameters["vae"] = vaeNames[0];
            foreach (var name in vaeNames)
            {
                resources.Add(new GenerationResource(GenerationResourceKind.Vae, name, null, null));
            }
        }

        foreach (var (name, weight) in loras)
        {
            resources.Add(new GenerationResource(GenerationResourceKind.Lora, name, null, weight));
        }

        if (samplers.Count > 0)
        {
            foreach (var entry in samplers[0])
            {
                parameters[entry.Key] = entry.Value;
            }
        }

        if (sizes.Count > 0)
        {
            var firstSize = sizes[0];
            parameters["size"] = firstSize.Size;
            if (firstSize.Batch is not null)
            {
                parameters["batch_size"] = firstSize.Batch;
            }
        }

        if (nodeCount > 20 || unrecognizedCount > 5 || samplers.Count > 1
            || checkpointNames.Count > 1 || texts.Count > 2 || sizes.Count > 1)
        {
            warnings.Add("工作流复杂，仅展示可证实节点值，未推断最终组合顺序。");
        }

        if (samplers.Count > 1)
        {
            warnings.Add("存在多个KSampler节点，仅保留首个可证实值。");
        }

        if (texts.Count > 2)
        {
            warnings.Add("存在多个文本编码节点，仅保留前两个。");
        }

        return new GenerationInfo
        {
            Source = GenerationSource.ComfyUi,
            PositivePrompt = positive,
            NegativePrompt = negative,
            Parameters = parameters,
            Resources = resources,
            RawEntries = rawEntries,
            Warnings = warnings,
        };
    }

    private static void ExtractFromPromptFormat(
        JsonElement root,
        List<(string NodeId, string Text)> texts,
        List<string> checkpointNames,
        List<string> vaeNames,
        List<(string Name, string? Weight)> loras,
        List<Dictionary<string, string>> samplers,
        List<(string Size, string? Batch)> sizes,
        ref int nodeCount,
        ref int unrecognizedCount,
        List<string> warnings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            warnings.Add("prompt JSON根不是对象，已保留原文。");
            return;
        }

        foreach (var node in root.EnumerateObject())
        {
            if (node.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            nodeCount++;
            var classType = GetString(node.Value, "class_type") ?? GetString(node.Value, "type");
            if (classType is null)
            {
                unrecognizedCount++;
                continue;
            }

            var inputs = node.Value.TryGetProperty("inputs", out var inputsElement)
                && inputsElement.ValueKind == JsonValueKind.Object
                    ? inputsElement
                    : (JsonElement?)null;

            switch (classType)
            {
                case "CLIPTextEncode":
                {
                    var text = inputs is { } element ? GetString(element, "text") : null;
                    if (text is not null)
                    {
                        texts.Add((node.Name, text));
                    }
                    else
                    {
                        warnings.Add($"文本编码节点“{node.Name}”缺少可证实的text值。");
                    }

                    break;
                }

                case "CheckpointLoaderSimple":
                case "CheckpointLoader":
                {
                    var name = inputs is { } element ? GetString(element, "ckpt_name") : null;
                    if (name is not null)
                    {
                        checkpointNames.Add(name);
                    }

                    break;
                }

                case "VAELoader":
                {
                    var name = inputs is { } element ? GetString(element, "vae_name") : null;
                    if (name is not null)
                    {
                        vaeNames.Add(name);
                    }

                    break;
                }

                case "LoraLoader":
                {
                    if (inputs is not { } element)
                    {
                        break;
                    }

                    var name = GetString(element, "lora_name");
                    if (name is null)
                    {
                        break;
                    }

                    loras.Add((name, CombineStrengths(element)));
                    break;
                }

                case "KSampler":
                case "KSamplerAdvanced":
                {
                    if (inputs is not { } element)
                    {
                        break;
                    }

                    samplers.Add(ExtractSampler(element));
                    break;
                }

                case "EmptyLatentImage":
                {
                    if (inputs is not { } element)
                    {
                        break;
                    }

                    var size = ExtractSize(element);
                    if (size is not null)
                    {
                        sizes.Add(size.Value);
                    }

                    break;
                }

                default:
                    unrecognizedCount++;
                    break;
            }
        }
    }

    private static void ExtractFromWorkflowFormat(
        JsonElement nodes,
        List<(string NodeId, string Text)> texts,
        List<string> checkpointNames,
        List<string> vaeNames,
        List<(string Name, string? Weight)> loras,
        List<(string Size, string? Batch)> sizes,
        ref int nodeCount,
        ref int unrecognizedCount,
        List<string> warnings)
    {
        var index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            index++;
            if (node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            nodeCount++;
            var nodeId = node.TryGetProperty("id", out var idElement)
                ? idElement.ToString()
                : index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var type = GetString(node, "type") ?? GetString(node, "class_type");
            if (type is null)
            {
                unrecognizedCount++;
                continue;
            }

            if (!node.TryGetProperty("widgets_values", out var widgets)
                || widgets.ValueKind != JsonValueKind.Array)
            {
                // Workflow nodes without positional widgets carry no confirmable
                // scalar here; links between nodes are intentionally not followed.
                if (type is not ("KSampler" or "KSamplerAdvanced" or "EmptyLatentImage"))
                {
                    unrecognizedCount++;
                }

                continue;
            }

            switch (type)
            {
                case "CLIPTextEncode":
                {
                    var text = GetArrayString(widgets, 0);
                    if (text is not null)
                    {
                        texts.Add((nodeId, text));
                    }
                    else
                    {
                        warnings.Add($"文本编码节点“{nodeId}”缺少可证实的text值。");
                    }

                    break;
                }

                case "CheckpointLoaderSimple":
                case "CheckpointLoader":
                {
                    var name = GetArrayString(widgets, 0);
                    if (name is not null)
                    {
                        checkpointNames.Add(name);
                    }

                    break;
                }

                case "VAELoader":
                {
                    var name = GetArrayString(widgets, 0);
                    if (name is not null)
                    {
                        vaeNames.Add(name);
                    }

                    break;
                }

                case "LoraLoader":
                {
                    var name = GetArrayString(widgets, 0);
                    if (name is null)
                    {
                        break;
                    }

                    var modelStrength = GetArrayScalar(widgets, 1);
                    var clipStrength = GetArrayScalar(widgets, 2);
                    loras.Add((name, modelStrength is null ? null : CombineScalars(modelStrength, clipStrength)));
                    break;
                }

                case "EmptyLatentImage":
                {
                    var width = GetArrayScalar(widgets, 0);
                    var height = GetArrayScalar(widgets, 1);
                    var batch = GetArrayScalar(widgets, 2);
                    if (width is not null && height is not null)
                    {
                        sizes.Add(($"{width}x{height}", batch));
                    }

                    break;
                }

                case "KSampler":
                case "KSamplerAdvanced":
                    // Positional sampler widgets differ across front-end versions;
                    // only unambiguously named prompt-format inputs are trusted.
                    warnings.Add("workflow格式的采样器节点位置不确定，未推断其组合。");
                    break;

                default:
                    unrecognizedCount++;
                    break;
            }
        }
    }

    private static Dictionary<string, string> ExtractSampler(JsonElement inputs)
    {
        var sampler = new Dictionary<string, string>(StringComparer.Ordinal);
        AddScalar(inputs, sampler, "seed", "seed");
        AddScalar(inputs, sampler, "steps", "steps");
        AddScalar(inputs, sampler, "cfg", "cfg");
        AddScalar(inputs, sampler, "sampler_name", "sampler");
        AddScalar(inputs, sampler, "scheduler", "scheduler");
        AddScalar(inputs, sampler, "denoise", "denoise");
        return sampler;
    }

    private static (string Size, string? Batch)? ExtractSize(JsonElement inputs)
    {
        var width = GetScalar(inputs, "width");
        var height = GetScalar(inputs, "height");
        if (width is null || height is null)
        {
            return null;
        }

        return ($"{width}x{height}", GetScalar(inputs, "batch_size"));
    }

    private static string? CombineStrengths(JsonElement inputs)
    {
        var model = GetScalar(inputs, "strength_model") ?? GetScalar(inputs, "strength");
        var clip = GetScalar(inputs, "strength_clip");
        return CombineScalars(model, clip);
    }

    private static string? CombineScalars(string? first, string? second)
    {
        if (first is null)
        {
            return null;
        }

        if (second is null || string.Equals(first, second, StringComparison.Ordinal))
        {
            return first;
        }

        return $"{first}/{second}";
    }

    private static void AddScalar(JsonElement inputs, Dictionary<string, string> target, string property, string key)
    {
        var value = GetScalar(inputs, property);
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? GetScalar(JsonElement inputs, string property)
    {
        if (!inputs.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static string? GetArrayString(JsonElement widgets, int index)
    {
        if (widgets.GetArrayLength() <= index)
        {
            return null;
        }

        var value = widgets[index];
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string? GetArrayScalar(JsonElement widgets, int index)
    {
        if (widgets.GetArrayLength() <= index)
        {
            return null;
        }

        return widgets[index].ValueKind switch
        {
            JsonValueKind.String => widgets[index].GetString(),
            JsonValueKind.Number => widgets[index].GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static int CompareNodeIds(string left, string right)
    {
        var leftIsInt = int.TryParse(left, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var leftId);
        var rightIsInt = int.TryParse(right, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var rightId);
        return (leftIsInt, rightIsInt) switch
        {
            (true, true) => leftId.CompareTo(rightId),
            _ => string.CompareOrdinal(left, right),
        };
    }

    private static string Truncate(string value) => value.Length <= GenerationMetadataReader.MaxFieldLength
        ? value
        : value.Substring(0, GenerationMetadataReader.MaxFieldLength);
}
