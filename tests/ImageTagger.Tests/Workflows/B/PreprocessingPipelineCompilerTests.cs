using ImageTagger.Core;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Infrastructure.Preprocessing;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

public sealed class PreprocessingPipelineCompilerTests
{
    [Fact]
    public void Compiles_valid_wd_pipeline_and_reuses_normalized_plan_once()
    {
        var counting = StandardPreprocessOperatorFactory.AllOperatorNames
            .Select(op => new CountingFactory(new StandardPreprocessOperatorFactory(op)))
            .ToArray();
        var compiler = new PreprocessingPipelineCompiler(counting);

        var first = compiler.ValidateAndCompile(ValidPipeline(), Input(448));
        var second = compiler.ValidateAndCompile(ValidPipeline(reverseDecodeParameters: true), Input(448));

        Assert.Same(first, second);
        Assert.Equal(11, first.Operators.Count);
        Assert.Equal([3, 448, 448], first.PerSampleTensorContract.Shape);
        Assert.Equal(64, first.Fingerprint.Length);
        Assert.Equal(11, counting.Sum(factory => factory.CompileCount));
    }

    [Fact]
    public void Compiles_v2_pipeline_with_generic_geometry_operators()
    {
        var compiled = new PreprocessingPipelineCompiler().ValidateAndCompile(V2Pipeline(64), Input(64));

        Assert.Equal(10, compiled.Operators.Count);
        Assert.Equal([3, 64, 64], compiled.PerSampleTensorContract.Shape);
        Assert.Equal("resize-fit", compiled.Steps[4].Op);
        Assert.Equal("pad-to-size", compiled.Steps[5].Op);
    }

    [Fact]
    public void Compiles_v2_pipeline_with_rectangular_geometry()
    {
        var steps = V2Pipeline(64).OrderedSteps.ToArray();
        steps[4] = Step("resize-fit",
            ("width", 48), ("height", 80), ("sampler", "pillow-bilinear-v1"), ("rounding", "floor"));
        steps[5] = Step("pad-to-size",
            ("width", 48), ("height", 80), ("anchor", "floor-center"), ("background", new[] { 0, 0, 0 }));
        var pipeline = V2Pipeline(64) with { OrderedSteps = steps };
        var input = new ModelInputTensor
        {
            DType = TensorDType.Float32,
            Layout = "NCHW",
            Shape = [3, 80, 48],
        };

        var compiled = new PreprocessingPipelineCompiler().ValidateAndCompile(pipeline, input);

        Assert.Equal([3, 80, 48], compiled.PerSampleTensorContract.Shape);
    }

    [Fact]
    public void Parameter_or_input_change_produces_a_distinct_fingerprint()
    {
        var compiler = new PreprocessingPipelineCompiler();
        var first = compiler.ValidateAndCompile(ValidPipeline(448), Input(448));
        var second = compiler.ValidateAndCompile(ValidPipeline(224), Input(224));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void Two_valid_mock_manifests_prove_step_order_and_parameters_control_the_plan()
    {
        var compiler = new PreprocessingPipelineCompiler();
        var original = ValidPipeline();
        var changedSteps = original.OrderedSteps.ToArray();
        (changedSteps[8], changedSteps[9]) = (changedSteps[9], changedSteps[8]);
        changedSteps[8] = changedSteps[8] with
        {
            Parameters = new Dictionary<string, object?>(changedSteps[8].Parameters, StringComparer.Ordinal)
            {
                ["mean"] = new[] { 0.4, 0.4, 0.4 },
            },
        };
        var changed = original with { OrderedSteps = changedSteps };

        var first = compiler.ValidateAndCompile(original, Input(448));
        var second = compiler.ValidateAndCompile(changed, Input(448));

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal("divide", first.Steps[8].Op);
        Assert.Equal("normalize", second.Steps[8].Op);
        Assert.Equal(0.4, ((double[])second.Steps[8].Parameters["mean"]!)[0]);
    }

    [Theory]
    [MemberData(nameof(InvalidPipelines))]
    public void Rejects_unknown_version_type_order_missing_tensor_and_shape_mismatch(
        PreprocessingPipelineDescriptor pipeline,
        ModelInputTensor input)
    {
        var exception = Assert.Throws<TaggerException>(
            () => new PreprocessingPipelineCompiler().ValidateAndCompile(pipeline, input));

        Assert.Equal(TaggerErrorCode.ModelPackInvalid, exception.Code);
    }

    public static TheoryData<PreprocessingPipelineDescriptor, ModelInputTensor> InvalidPipelines()
    {
        var unknown = ValidPipeline();
        unknown.OrderedSteps[1] = Step("run-script");

        var badVersion = ValidPipeline();
        badVersion.OrderedSteps[1] = badVersion.OrderedSteps[1] with { Version = 2 };

        var duplicateDecode = ValidPipeline();
        duplicateDecode.OrderedSteps[1] = Step("decode", ("frame", "first"), ("colorManagement", "ignore"));

        var badOrder = ValidPipeline();
        (badOrder.OrderedSteps[6], badOrder.OrderedSteps[7]) =
            (badOrder.OrderedSteps[7], badOrder.OrderedSteps[6]);

        var noTensor = new PreprocessingPipelineDescriptor
        {
            SchemaVersion = 1,
            OrderedSteps =
            [
                Step("decode", ("frame", "first"), ("colorManagement", "ignore")),
                Step("exif-transpose"),
            ],
        };

        var complete = ValidPipeline();
        var noPermute = complete with { OrderedSteps = complete.OrderedSteps[..^1] };

        // v2 算子出现在 v1 流水线中被编译器拒绝（manifest schema 是规范门禁，编译器仍需防御）。
        var v2OpInV1 = ValidPipeline();
        v2OpInV1.OrderedSteps[5] = Step("resize-fit",
            ("width", 64), ("height", 64), ("sampler", "pillow-bilinear-v1"), ("rounding", "floor"));

        // v2 流水线最终形状无法静态确定（缺少 pad/crop/resize 收敛）。
        var openFinalShape = V2Pipeline(64) with
        {
            OrderedSteps =
            [
                Step("decode", ("frame", "first"), ("colorManagement", "ignore")),
                Step("ensure-color", ("mode", "rgb")),
                Step("resize-fit", ("width", 64), ("height", 64), ("sampler", "pillow-bilinear-v1"), ("rounding", "floor")),
                Step("cast", ("dtype", "float32")),
                Step("permute", ("order", new[] { 2, 0, 1 })),
            ],
        };

        return new TheoryData<PreprocessingPipelineDescriptor, ModelInputTensor>
        {
            { unknown, Input(448) },
            { badVersion, Input(448) },
            { duplicateDecode, Input(448) },
            { badOrder, Input(448) },
            { noTensor, Input(448) },
            { noPermute, Input(448) },
            { ValidPipeline(), Input(224) },
            { v2OpInV1, Input(448) },
            { openFinalShape, Input(64) },
        };
    }

    internal static PreprocessingPipelineDescriptor ValidPipeline(
        int size = 448,
        bool reverseDecodeParameters = false)
    {
        var decode = reverseDecodeParameters
            ? Step("decode", ("colorManagement", "ignore"), ("frame", "first"))
            : Step("decode", ("frame", "first"), ("colorManagement", "ignore"));
        return new PreprocessingPipelineDescriptor
        {
            SchemaVersion = 1,
            OrderedSteps =
            [
                decode,
                Step("exif-transpose"),
                Step("ensure-color", ("mode", "rgb-or-rgba-if-transparent")),
                Step("alpha-composite", ("when", "has-alpha"), ("background", new[] { 255, 255, 255 })),
                Step("pad-to-square", ("anchor", "floor-center"), ("background", new[] { 255, 255, 255 })),
                Step("resize", ("width", size), ("height", size), ("sampler", "pillow-bicubic-v1")),
                Step("reorder-channels", ("order", new[] { 2, 1, 0 })),
                Step("cast", ("dtype", "float32")),
                Step("divide", ("value", 255d)),
                Step("normalize", ("mean", new[] { 0.5, 0.5, 0.5 }), ("std", new[] { 0.5, 0.5, 0.5 })),
                Step("permute", ("order", new[] { 2, 0, 1 })),
            ],
        };
    }

    private static PreprocessStepDescriptor Step(
        string op,
        params (string Name, object? Value)[] parameters) => new()
    {
        Op = op,
        Version = 1,
        Parameters = parameters.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal),
    };

    internal static ModelInputTensor Input(int size) => new()
    {
        DType = TensorDType.Float32,
        Layout = "NCHW",
        Shape = [3, size, size],
    };

    internal static PreprocessingPipelineDescriptor V2Pipeline(int size = 64) => new()
    {
        SchemaVersion = 2,
        OrderedSteps =
        [
            Step("decode", ("frame", "first"), ("colorManagement", "ignore")),
            Step("exif-transpose"),
            Step("ensure-color", ("mode", "rgb-or-rgba-if-transparent")),
            Step("alpha-composite", ("when", "has-alpha"), ("background", new[] { 255, 255, 255 })),
            Step("resize-fit", ("width", size), ("height", size), ("sampler", "pillow-bilinear-v1"), ("rounding", "floor")),
            Step("pad-to-size", ("width", size), ("height", size), ("anchor", "floor-center"), ("background", new[] { 0, 0, 0 })),
            Step("cast", ("dtype", "float32")),
            Step("divide", ("value", 255d)),
            Step("normalize", ("mean", new[] { 0.5, 0.5, 0.5 }), ("std", new[] { 0.5, 0.5, 0.5 })),
            Step("permute", ("order", new[] { 2, 0, 1 })),
        ],
    };

    private sealed class CountingFactory(IPreprocessOperatorFactory inner) : IPreprocessOperatorFactory
    {
        public int CompileCount { get; private set; }
        public string Op => inner.Op;
        public IReadOnlySet<int> SupportedVersions => inner.SupportedVersions;
        public void ValidateParameters(IReadOnlyDictionary<string, object?> parameters) =>
            inner.ValidateParameters(parameters);
        public (TensorKind Input, TensorKind Output) GetKinds(IReadOnlyDictionary<string, object?> parameters) =>
            inner.GetKinds(parameters);
        public (int Width, int Height, int Channels) InferShape(
            (int Width, int Height, int Channels) input,
            IReadOnlyDictionary<string, object?> parameters) => inner.InferShape(input, parameters);
        public IPreprocessOperator Compile(IReadOnlyDictionary<string, object?> parameters)
        {
            CompileCount++;
            return inner.Compile(parameters);
        }
    }
}
