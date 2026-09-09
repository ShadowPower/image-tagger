using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Metadata;
using Xunit;

namespace ImageTagger.Tests.Workflows.E;

public sealed class A1111ParserTests
{
    private const string SampleParameters = """
        masterpiece, best quality, 1girl, sunset over beach
        Negative prompt: lowres, bad anatomy, (bad hands:1.2), "weird, thing"
        Steps: 28, Sampler: DPM++ 2M, Schedule type: Karras, CFG scale: 7, Seed: 123456789, Size: 1024x1536, Model hash: a1b2c3d4e5, Model: animagine_v4, VAE: vaeFtMse840000, Clip skip: 2, Hires upscale: 2, Hires upscaler: Latent, Denoising strength: 0.5
        """;

    [Fact]
    [Trait("Category", "Unit")]
    public void Splits_positive_and_negative_prompts_verbatim()
    {
        var info = Automatic1111Parser.Parse(SampleParameters);

        Assert.NotNull(info);
        Assert.Equal(GenerationSource.Automatic1111, info.Source);
        Assert.Equal("masterpiece, best quality, 1girl, sunset over beach", info.PositivePrompt);
        Assert.Equal("lowres, bad anatomy, (bad hands:1.2), \"weird, thing\"", info.NegativePrompt);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Parses_common_tail_parameters_and_resources()
    {
        var info = Automatic1111Parser.Parse(SampleParameters);

        Assert.NotNull(info);
        Assert.Equal("28", info.Parameters["steps"]);
        Assert.Equal("DPM++ 2M", info.Parameters["sampler"]);
        Assert.Equal("Karras", info.Parameters["scheduler"]);
        Assert.Equal("7", info.Parameters["cfg"]);
        Assert.Equal("123456789", info.Parameters["seed"]);
        Assert.Equal("1024x1536", info.Parameters["size"]);
        Assert.Equal("a1b2c3d4e5", info.Parameters["model_hash"]);
        Assert.Equal("animagine_v4", info.Parameters["model"]);
        Assert.Equal("vaeFtMse840000", info.Parameters["vae"]);
        Assert.Equal("2", info.Parameters["clip_skip"]);
        Assert.Equal("2", info.Parameters["hires_upscale"]);
        Assert.Equal("Latent", info.Parameters["hires_upscaler"]);
        Assert.Equal("0.5", info.Parameters["denoising"]);
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Checkpoint
            && resource.Name == "animagine_v4"
            && resource.Hash == "a1b2c3d4e5");
        Assert.Contains(info.Resources, resource =>
            resource.Kind == GenerationResourceKind.Vae && resource.Name == "vaeFtMse840000");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Preserves_unrecognized_fields_verbatim()
    {
        const string text = "a cat\nNegative prompt: blurry\nSteps: 20, Sampler: Euler, FooBar: xyz 123, Custom Thing: 1";

        var info = Automatic1111Parser.Parse(text);

        Assert.NotNull(info);
        Assert.Equal("20", info.Parameters["steps"]);
        Assert.Equal("Euler", info.Parameters["sampler"]);
        Assert.Equal("xyz 123", info.RawEntries["FooBar"]);
        Assert.Equal("1", info.RawEntries["Custom Thing"]);
        Assert.Equal(text, info.RawEntries["parameters"]);
        Assert.NotEmpty(info.Warnings);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Detects_forge_source_marker()
    {
        const string text = "a cat\nNegative prompt: blurry\nSteps: 20, Sampler: Euler, Version: Forge 1.0";

        var info = Automatic1111Parser.Parse(text);

        Assert.NotNull(info);
        Assert.Equal(GenerationSource.Forge, info.Source);
        Assert.Equal("Forge 1.0", info.SoftwareVersion);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Does_not_split_commas_inside_quotes_or_parentheses()
    {
        const string text = "prompt\nNegative prompt: neg\nSteps: 20, Sampler: DPM++ 2M (a, b), CFG scale: 7, Foo: \"x, y\", Seed: 5";

        var info = Automatic1111Parser.Parse(text);

        Assert.NotNull(info);
        Assert.Equal("DPM++ 2M (a, b)", info.Parameters["sampler"]);
        Assert.Equal("\"x, y\"", info.RawEntries["Foo"]);
        Assert.Equal("5", info.Parameters["seed"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Returns_null_for_empty_input_and_warns_without_tail_line()
    {
        Assert.Null(Automatic1111Parser.Parse(null));
        Assert.Null(Automatic1111Parser.Parse("   "));

        var info = Automatic1111Parser.Parse("just a positive prompt");
        Assert.NotNull(info);
        Assert.Equal("just a positive prompt", info.PositivePrompt);
        Assert.Null(info.NegativePrompt);
        Assert.NotEmpty(info.Warnings);
    }
}
