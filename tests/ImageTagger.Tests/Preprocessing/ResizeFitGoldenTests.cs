using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Tests.TestData;
using Xunit;

namespace ImageTagger.Tests.Preprocessing;

/// <summary>
/// Golden baselines for the generic `resize-fit` + `pad-to-size` composition
/// (tools/golden/export_golden_fit.py): aspect-preserving fit resize with the
/// Pillow BILINEAR kernel, then a floor-center black pad. PNG fixtures are
/// lossless, so both the sampler stage and the full declarative executor must
/// match the Pillow reference bit-exactly.
/// </summary>
public class ResizeFitGoldenTests
{
    private const int Size = 256;

    public static TheoryData<string> FixtureNames()
    {
        var names = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(GoldenDir, "*.fit_tensor.npy").OrderBy(f => f, StringComparer.Ordinal))
            names.Add(Path.GetFileName(file).Replace(".fit_tensor.npy", string.Empty, StringComparison.Ordinal));
        return names;
    }

    internal static string GoldenDir => Path.Combine(
        PreprocessingGoldenTests.RepoRoot, "tests", "ImageTagger.Tests", "TestData", "golden-fit");

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Fit_resize_matches_pillow_bilinear_bit_exactly(string fixtureStem)
    {
        var stages = PillowReferencePreprocessor.Run(FixturePath(fixtureStem), Size);
        var (width, height) = ResizeFitGeometry.Fit(stages.Width, stages.Height, Size, Size, "floor");
        var resized = new byte[width * height * 3];
        PillowBilinearResampler.ResizeRgb(stages.EnsureRgb, stages.Width, stages.Height, resized, width, height);

        var golden = NpyFile.Load(Path.Combine(GoldenDir, $"{fixtureStem}.fit_resized.npy"));
        Assert.Equal([height, width, 3], golden.Shape);
        var want = golden.ToBytes();
        Assert.Equal(want.Length, resized.Length);

        int maxDiff = 0;
        for (int i = 0; i < want.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(want[i] - resized[i]));
        Assert.Equal(0, maxDiff);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task V2_pipeline_tensor_matches_golden_exactly(string fixtureStem)
    {
        var compiled = new PreprocessingPipelineCompiler().ValidateAndCompile(
            V2Pipeline(),
            new ModelInputTensor
            {
                DType = TensorDType.Float32,
                Layout = "NCHW",
                Shape = [3, Size, Size],
            });

        var values = new float[3 * Size * Size];
        await new StandardPreprocessingExecutor().PreprocessIntoAsync(
            compiled,
            new FileImageSource(FixturePath(fixtureStem)),
            values,
            TestContext.Current.CancellationToken);

        var golden = NpyFile.Load(Path.Combine(GoldenDir, $"{fixtureStem}.fit_tensor.npy")).ToFloats();
        Assert.Equal(golden.Length, values.Length);

        float maxDiff = 0;
        for (int i = 0; i < golden.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(golden[i] - values[i]));
        Assert.Equal(0f, maxDiff);
    }

    private static string FixturePath(string fixtureStem) =>
        Path.Combine(PreprocessingGoldenTests.FixturesDir,
            Directory.GetFiles(PreprocessingGoldenTests.FixturesDir, fixtureStem + ".*")
                .Select(path => Path.GetFileName(path)!)
                .Single());

    private static PreprocessingPipelineDescriptor V2Pipeline() => new()
    {
        SchemaVersion = ModelDescriptor.MaximumSchemaVersion,
        OrderedSteps =
        [
            Step("decode", ("frame", "first"), ("colorManagement", "ignore")),
            Step("exif-transpose"),
            Step("ensure-color", ("mode", "rgb-or-rgba-if-transparent")),
            Step("alpha-composite", ("when", "has-alpha"), ("background", new[] { 255, 255, 255 })),
            Step("resize-fit", ("width", Size), ("height", Size), ("sampler", "pillow-bilinear-v1"), ("rounding", "floor")),
            Step("pad-to-size", ("width", Size), ("height", Size), ("anchor", "floor-center"), ("background", new[] { 0, 0, 0 })),
            Step("cast", ("dtype", "float32")),
            Step("divide", ("value", 255d)),
            Step("normalize", ("mean", new[] { 0.5, 0.5, 0.5 }), ("std", new[] { 0.5, 0.5, 0.5 })),
            Step("permute", ("order", new[] { 2, 0, 1 })),
        ],
    };

    private static PreprocessStepDescriptor Step(string op, params (string Name, object? Value)[] parameters) => new()
    {
        Op = op,
        Version = 1,
        Parameters = parameters.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal),
    };
}
