using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Metadata;
using Xunit;

namespace ImageTagger.Tests.Workflows.E;

public sealed class GenerationInfoParserTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Merges_multiple_sources_first_prompt_wins_and_all_values_kept()
    {
        const string comfyPrompt = """
            {
              "1": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "model_b.safetensors" } },
              "2": { "class_type": "CLIPTextEncode", "inputs": { "text": "comfy cat" } },
              "3": { "class_type": "KSampler", "inputs": { "seed": 999, "steps": 30 } },
              "4": { "class_type": "EmptyLatentImage", "inputs": { "width": 512, "height": 512, "batch_size": 1 } }
            }
            """;
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Png/parameters"] = "a sunny cat\nNegative prompt: blurry\nSteps: 28, Sampler: Euler, Model hash: abc123, Model: model_a",
            ["Png/prompt"] = comfyPrompt,
        };

        var info = GenerationInfoParser.ParseRaw(raw);

        Assert.NotNull(info);
        Assert.Equal("a sunny cat", info.PositivePrompt);
        Assert.Equal("blurry", info.NegativePrompt);
        Assert.Equal("28", info.Parameters["steps"]);
        Assert.Equal("Euler", info.Parameters["sampler"]);
        Assert.Equal("999", info.Parameters["seed"]);
        Assert.Equal("512x512", info.Parameters["size"]);
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Checkpoint && resource.Name == "model_a");
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Checkpoint && resource.Name == "model_b.safetensors");
        Assert.Contains(info.Warnings, warning => warning.Contains("多套", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Same_payload_under_two_reader_keys_dedupes_resources()
    {
        // MetadataExtractor 与 ImageSharp 可能以上不同键报出同一份 parameters 文本；
        // 合并后参数天然去重，资源列表也必须按值去重，否则界面出现重复行。
        const string a1111 = "a cat\nNegative prompt: blurry\nSteps: 20, Sampler: Euler, Model hash: abc123, Model: model_a";
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Png/parameters"] = a1111,
            ["ImageSharp/parameters"] = a1111,
        };

        var info = GenerationInfoParser.ParseRaw(raw);

        Assert.NotNull(info);
        Assert.Single(info.Resources);
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Checkpoint && resource.Name == "model_a");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Corrupt_field_keeps_partial_result_with_warning()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Png/parameters"] = "a cat\nNegative prompt: blurry\nSteps: 20, Sampler: Euler",
            ["Png/workflow"] = "{broken json",
        };

        var info = GenerationInfoParser.ParseRaw(raw);

        Assert.NotNull(info);
        Assert.Equal("a cat", info.PositivePrompt);
        Assert.Equal("20", info.Parameters["steps"]);
        Assert.NotEmpty(info.Warnings);
        Assert.Equal("{broken json", info.RawEntries["Png/workflow"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Single_field_over_2mib_is_truncated_with_warning()
    {
        var hugePositive = new string('x', GenerationMetadataReader.MaxFieldLength + 100);
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Png/parameters"] = hugePositive + "\nSteps: 20",
        };

        var info = GenerationInfoParser.ParseRaw(raw);

        Assert.NotNull(info);
        Assert.NotNull(info.PositivePrompt);
        Assert.Equal(GenerationMetadataReader.MaxFieldLength, info.PositivePrompt.Length);
        Assert.Contains(info.Warnings, warning => warning.Contains("2MiB", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Json_beyond_max_depth_is_preserved_with_warning()
    {
        var deepJson = new string('[', 70) + new string(']', 70);
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Png/workflow"] = deepJson,
        };

        var info = GenerationInfoParser.ParseRaw(raw);

        Assert.NotNull(info);
        Assert.Contains(info.Warnings, warning => warning.Contains("深度", StringComparison.Ordinal));
        Assert.Equal(deepJson, info.RawEntries["Png/workflow"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Unknown_sources_preserve_discovered_keys_and_reader_warnings()
    {
        var raw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Exif/Software"] = "SomeTool",
            ["Exif/ImageDescription"] = "hello",
            ["Warning/0"] = "earlier truncation note",
        };

        var info = GenerationInfoParser.ParseRaw(raw);

        Assert.NotNull(info);
        Assert.Equal(GenerationSource.Unknown, info.Source);
        Assert.Equal("SomeTool", info.RawEntries["Exif/Software"]);
        Assert.Equal("hello", info.RawEntries["Exif/ImageDescription"]);
        Assert.DoesNotContain("Warning/0", info.RawEntries.Keys);
        Assert.Contains("earlier truncation note", info.Warnings);
        Assert.Contains(info.Warnings, warning => warning.Contains("未发现支持", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Returns_null_without_any_entries()
    {
        Assert.Null(GenerationInfoParser.ParseRaw(null));
        Assert.Null(GenerationInfoParser.ParseRaw(new Dictionary<string, string>(StringComparer.Ordinal)));
        Assert.Null(GenerationInfoParser.ParseRaw(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Warning/0"] = "only a warning",
        }));
    }
}
