using System.Globalization;
using System.Text.Json;
using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Metadata;
using Xunit;

namespace ImageTagger.Tests.Workflows.E;

public sealed class ComfyUiParserTests
{
    private const string PromptJson = """
        {
          "1": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "animagine_v4.safetensors" } },
          "2": { "class_type": "CLIPTextEncode", "inputs": { "text": "masterpiece, 1girl, sunset" } },
          "3": { "class_type": "CLIPTextEncode", "inputs": { "text": "lowres, bad anatomy" } },
          "4": { "class_type": "VAELoader", "inputs": { "vae_name": "vaeFtMse840000.safetensors" } },
          "5": { "class_type": "LoraLoader", "inputs": { "lora_name": "detail_lora.safetensors", "strength_model": 0.8, "strength_clip": 0.8 } },
          "6": { "class_type": "KSampler", "inputs": { "seed": 123456789, "steps": 28, "cfg": 7.0, "sampler_name": "dpmpp_2m", "scheduler": "karras", "denoise": 1.0 } },
          "7": { "class_type": "EmptyLatentImage", "inputs": { "width": 1024, "height": 1536, "batch_size": 1 } }
        }
        """;

    [Fact]
    [Trait("Category", "Unit")]
    public void Extracts_prompt_nodes_into_normalized_info()
    {
        var info = ComfyUiParser.Parse(PromptJson);

        Assert.NotNull(info);
        Assert.Equal(GenerationSource.ComfyUi, info.Source);
        Assert.Equal("masterpiece, 1girl, sunset", info.PositivePrompt);
        Assert.Equal("lowres, bad anatomy", info.NegativePrompt);
        Assert.Equal("123456789", info.Parameters["seed"]);
        Assert.Equal("28", info.Parameters["steps"]);
        Assert.Equal("7.0", info.Parameters["cfg"]);
        Assert.Equal("dpmpp_2m", info.Parameters["sampler"]);
        Assert.Equal("karras", info.Parameters["scheduler"]);
        Assert.Equal("1.0", info.Parameters["denoise"]);
        Assert.Equal("1024x1536", info.Parameters["size"]);
        Assert.Equal("1", info.Parameters["batch_size"]);
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Checkpoint && resource.Name == "animagine_v4.safetensors");
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Vae && resource.Name == "vaeFtMse840000.safetensors");
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Lora
            && resource.Name == "detail_lora.safetensors"
            && resource.Weight == "0.8");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Extracts_workflow_nodes_without_following_links()
    {
        const string workflowJson = """
            {
              "nodes": [
                { "id": 1, "type": "CLIPTextEncode", "widgets_values": ["a cat"] },
                { "id": 2, "type": "CLIPTextEncode", "widgets_values": ["blurry"] },
                { "id": 3, "type": "CheckpointLoaderSimple", "widgets_values": ["model_a.safetensors"] }
              ]
            }
            """;

        var info = ComfyUiParser.Parse(null, workflowJson);

        Assert.NotNull(info);
        Assert.Equal("a cat", info.PositivePrompt);
        Assert.Equal("blurry", info.NegativePrompt);
        Assert.Contains(info.Resources, resource => resource.Name == "model_a.safetensors");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Complex_workflow_reports_confirmable_values_plus_warning()
    {
        var trimmed = PromptJson.Trim();
        var inner = trimmed.Substring(1, trimmed.Length - 2);
        var builder = new System.Text.StringBuilder(inner.Length + 2048);
        builder.Append('{').Append(inner);
        for (var i = 10; i < 40; i++)
        {
            builder.Append(CultureInfo.InvariantCulture, $", \"{i}\": {{ \"class_type\": \"MysteryNode{i}\", \"inputs\": {{ }} }}");
        }

        builder.Append(", \"50\": { \"class_type\": \"KSampler\", \"inputs\": { \"seed\": 2, \"steps\": 10 } } }");

        var info = ComfyUiParser.Parse(builder.ToString());

        Assert.NotNull(info);
        Assert.Equal("123456789", info.Parameters["seed"]);
        Assert.NotEmpty(info.Warnings);
        Assert.Contains(info.Warnings, warning => warning.Contains("可证实", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Treats_paths_and_expressions_as_plain_text_without_executing()
    {
        const string tricky = """
            {
              "1": { "class_type": "CLIPTextEncode", "inputs": { "text": "ignore ${evil()} and secret-relative-path" } },
              "2": { "class_type": "CheckpointLoaderSimple", "inputs": { "ckpt_name": "weird;name|with*specials.safetensors" } }
            }
            """;

        var info = ComfyUiParser.Parse(tricky);

        Assert.NotNull(info);
        Assert.Equal("ignore ${evil()} and secret-relative-path", info.PositivePrompt);
        Assert.Contains(info.Resources, resource => resource.Name == "weird;name|with*specials.safetensors");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Returns_null_without_recognizable_nodes_or_input()
    {
        Assert.Null(ComfyUiParser.Parse(null));
        Assert.Null(ComfyUiParser.Parse("  "));
        Assert.Null(ComfyUiParser.Parse("{ \"1\": { \"class_type\": \"MysteryNode\", \"inputs\": { } } }"));
        Assert.Null(ComfyUiParser.Parse("{not json"));
    }
}
