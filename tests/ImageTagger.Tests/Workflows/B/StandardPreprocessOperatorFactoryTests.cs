using ImageTagger.Core;
using ImageTagger.Core.Pipelines;
using ImageTagger.Infrastructure.Preprocessing;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

public sealed class StandardPreprocessOperatorFactoryTests
{
    public static TheoryData<string, IReadOnlyDictionary<string, object?>> ValidOperators() => new()
    {
        { "decode", Parameters(("frame", "first"), ("colorManagement", "ignore")) },
        { "exif-transpose", Parameters() },
        { "ensure-color", Parameters(("mode", "rgb-or-rgba-if-transparent")) },
        { "alpha-composite", Parameters(("when", "has-alpha"), ("background", new[] { 255, 255, 255 })) },
        { "pad-to-square", Parameters(("anchor", "floor-center"), ("background", new object[] { 255L, 255L, 255L })) },
        { "resize", Parameters(("width", 448L), ("height", 448L), ("sampler", "pillow-bicubic-v1")) },
        { "crop", Parameters(("width", 224), ("height", 224), ("anchor", "center")) },
        { "reorder-channels", Parameters(("order", new[] { 2, 1, 0 })) },
        { "cast", Parameters(("dtype", "float32")) },
        { "divide", Parameters(("value", 255d)) },
        { "normalize", Parameters(("mean", new[] { 0.5, 0.5, 0.5 }), ("std", new[] { 0.5, 0.5, 0.5 })) },
        { "permute", Parameters(("order", new object[] { 2L, 0L, 1L })) },
    };

    [Theory]
    [MemberData(nameof(ValidOperators))]
    public void All_twelve_v1_operators_accept_their_closed_valid_parameters(
        string op,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var factory = new StandardPreprocessOperatorFactory(op);

        factory.ValidateParameters(parameters);

        Assert.Contains(1, factory.SupportedVersions);
        Assert.NotNull(factory.Compile(parameters));
    }

    [Fact]
    public void Registry_contains_exactly_the_design_operator_set_without_quantization()
    {
        var registry = StandardPreprocessOperatorRegistry.Create();

        Assert.Equal(12, registry.Count);
        Assert.Equal(StandardPreprocessOperatorFactory.AllOperatorNames.Order(), registry.Keys.Order());
        Assert.DoesNotContain(registry.Keys, op => op.Contains("quant", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(InvalidParameters))]
    public void Rejects_unknown_missing_mistyped_or_out_of_range_parameters(
        string op,
        IReadOnlyDictionary<string, object?> parameters)
    {
        var exception = Assert.Throws<TaggerException>(
            () => new StandardPreprocessOperatorFactory(op).ValidateParameters(parameters));

        Assert.Equal(TaggerErrorCode.ModelPackInvalid, exception.Code);
    }

    public static TheoryData<string, IReadOnlyDictionary<string, object?>> InvalidParameters() => new()
    {
        { "decode", Parameters(("frame", "first"), ("colorManagement", "ignore"), ("extra", true)) },
        { "decode", Parameters(("frame", "first")) },
        { "ensure-color", Parameters(("mode", "CMYK")) },
        { "alpha-composite", Parameters(("when", "has-alpha"), ("background", new[] { 0, 0 })) },
        { "resize", Parameters(("width", 2.5), ("height", 448), ("sampler", "pillow-bicubic-v1")) },
        { "crop", Parameters(("width", 224), ("height", 224), ("anchor", "top-left")) },
        { "reorder-channels", Parameters(("order", new[] { 0, 0, 2 })) },
        { "cast", Parameters(("dtype", "float16")) },
        { "divide", Parameters(("value", double.NaN)) },
        { "normalize", Parameters(("mean", new[] { 0.5, 0.5 }), ("std", new[] { 0.5, 0.5, 0.5 })) },
        { "normalize", Parameters(("mean", new[] { 0.5, 0.5, 0.5 }), ("std", new[] { 0.5, 0.0, 0.5 })) },
        { "permute", Parameters(("order", new[] { 3, 1, 0 })) },
    };

    [Fact]
    public void Shape_inference_uses_declared_geometry_and_rejects_impossible_crop()
    {
        var pad = new StandardPreprocessOperatorFactory("pad-to-square");
        var padded = pad.InferShape(
            (640, 480, 3), Parameters(("anchor", "floor-center"), ("background", new[] { 255, 255, 255 })));
        Assert.Equal((640, 640, 3), padded);

        var crop = new StandardPreprocessOperatorFactory("crop");
        Assert.Throws<TaggerException>(() => crop.InferShape(
            (100, 100, 3), Parameters(("width", 224), ("height", 224), ("anchor", "center"))));
    }

    [Fact]
    public void Compiles_a_clip_style_pipeline_using_approved_resize_and_crop_declarations()
    {
        var descriptor = new ImageTagger.Core.ModelPacks.PreprocessingPipelineDescriptor
        {
            SchemaVersion = 1,
            OrderedSteps =
            [
                Step("decode", ("frame", "first"), ("colorManagement", "ignore")),
                Step("exif-transpose"),
                Step("ensure-color", ("mode", "rgb")),
                Step("resize", ("width", 64), ("height", 64), ("sampler", "imagesharp-bicubic-v1")),
                Step("crop", ("width", 32), ("height", 32), ("anchor", "center")),
                Step("cast", ("dtype", "float32")),
                Step("divide", ("value", 255d)),
                Step("normalize", ("mean", new[] { 0.5, 0.5, 0.5 }), ("std", new[] { 0.5, 0.5, 0.5 })),
                Step("permute", ("order", new[] { 2, 0, 1 })),
            ],
        };
        var input = new ModelInputTensor
        {
            DType = TensorDType.Float32,
            Layout = "NCHW",
            Shape = [3, 32, 32],
        };

        var compiled = new PreprocessingPipelineCompiler().ValidateAndCompile(descriptor, input);

        Assert.Equal(["resize", "crop"], compiled.Steps.Skip(3).Take(2).Select(step => step.Op));
    }

    private static ImageTagger.Core.ModelPacks.PreprocessStepDescriptor Step(
        string op,
        params (string Name, object? Value)[] values) => new()
    {
        Op = op,
        Version = 1,
        Parameters = Parameters(values),
    };

    private static IReadOnlyDictionary<string, object?> Parameters(
        params (string Name, object? Value)[] values) =>
        values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
}
