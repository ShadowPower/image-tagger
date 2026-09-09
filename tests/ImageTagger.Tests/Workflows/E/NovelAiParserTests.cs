using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Metadata;
using Xunit;

namespace ImageTagger.Tests.Workflows.E;

public sealed class NovelAiParserTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Normalizes_comment_json_prompts_and_scalars()
    {
        const string comment = """
            {"prompt": "a girl, sunset, sea", "uc": "lowres, blurry", "steps": 28, "scale": 7, "seed": 12345, "sampler": "k_euler"}
            """;

        var info = NovelAiParser.Parse(comment, "NovelAI 1.0", null);

        Assert.Equal(GenerationSource.NovelAi, info.Source);
        Assert.Equal("a girl, sunset, sea", info.PositivePrompt);
        Assert.Equal("lowres, blurry", info.NegativePrompt);
        Assert.Equal("28", info.Parameters["steps"]);
        Assert.Equal("7", info.Parameters["scale"]);
        Assert.Equal("12345", info.Parameters["seed"]);
        Assert.Equal("k_euler", info.Parameters["sampler"]);
        Assert.Empty(info.Resources);
        Assert.Equal("NovelAI 1.0", info.SoftwareVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Recognizes_software_marker_even_with_minimal_comment()
    {
        var info = NovelAiParser.Parse(null, "NovelAI Diffusion", "picture");

        Assert.Equal(GenerationSource.NovelAi, info.Source);
        Assert.Equal("NovelAI Diffusion", info.RawEntries["software"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Unknown_fallback_preserves_discovered_keys()
    {
        const string comment = """{"foo": "bar", "count": 3}""";

        var info = NovelAiParser.Parse(comment, "SomeTool 2.0", "picture");

        Assert.Equal(GenerationSource.Unknown, info.Source);
        Assert.Equal(comment, info.RawEntries["comment"]);
        Assert.Equal("SomeTool 2.0", info.RawEntries["software"]);
        Assert.Equal("picture", info.RawEntries["source"]);
        Assert.NotEmpty(info.Warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Corrupt_comment_keeps_raw_text_with_warning()
    {
        var info = NovelAiParser.Parse("{broken json", "NovelAI 1.0", null);

        Assert.Equal(GenerationSource.NovelAi, info.Source);
        Assert.Equal("{broken json", info.RawEntries["comment"]);
        Assert.NotEmpty(info.Warnings);
    }
}
