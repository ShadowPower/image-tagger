using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Tests.TestData;
using Xunit;

namespace ImageTagger.Tests.Preprocessing;

/// <summary>
/// P-05 golden comparison: runs the C# preprocessing pipeline against per-stage
/// numpy baselines produced by the Pillow reference (tools/golden/export_golden.py).
/// Lossless fixtures must match exactly; JPEG differences are quantized and bounded.
/// </summary>
public class PreprocessingGoldenTests
{
    private readonly ITestOutputHelper _output;

    public PreprocessingGoldenTests(ITestOutputHelper output) => _output = output;
    public static TheoryData<string> FixtureNames()
    {
        var names = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(FixturesDir))
            names.Add(Path.GetFileName(file));
        return names;
    }

    internal static string RepoRoot => FindRepoRoot();
    internal static string FixturesDir => Path.Combine(RepoRoot, "tests", "ImageTagger.Tests", "TestData", "fixtures");
    internal static string GoldenDir => Path.Combine(RepoRoot, "tests", "ImageTagger.Tests", "TestData", "golden");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ImageTagger.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static bool IsJpeg(string name) =>
        name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Stage_ensure_rgb_matches_golden(string fixtureName)
    {
        var stages = PillowReferencePreprocessor.Run(Path.Combine(FixturesDir, fixtureName));
        CompareStage(fixtureName, "stage_ensure_rgb", stages.EnsureRgb, IsJpeg(fixtureName), _output);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Stage_padded_matches_golden(string fixtureName)
    {
        var stages = PillowReferencePreprocessor.Run(Path.Combine(FixturesDir, fixtureName));
        CompareStage(fixtureName, "stage_padded", stages.Padded, IsJpeg(fixtureName), _output);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Stage_resized_matches_golden(string fixtureName)
    {
        var stages = PillowReferencePreprocessor.Run(Path.Combine(FixturesDir, fixtureName));
        CompareStage(fixtureName, "stage_resized", stages.Resized, IsJpeg(fixtureName), _output);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Stage_tensor_matches_golden(string fixtureName)
    {
        var stages = PillowReferencePreprocessor.Run(Path.Combine(FixturesDir, fixtureName));
        var golden = NpyFile.Load(Path.Combine(GoldenDir, $"{Path.GetFileNameWithoutExtension(fixtureName)}.stage_tensor.npy"));
        var want = golden.ToFloats();
        Assert.Equal(want.Length, stages.Tensor.Length);

        float maxDiff = 0, sumDiff = 0;
        for (int i = 0; i < want.Length; i++)
        {
            float diff = Math.Abs(want[i] - stages.Tensor[i]);
            maxDiff = Math.Max(maxDiff, diff);
            sumDiff += diff;
        }
        // uint8 stage differences are amplified by at most 2/255 through normalization.
        float allowed = IsJpeg(fixtureName) ? 0.7f : 0f;
        _output.WriteLine($"[golden] {fixtureName,-28} {"stage_tensor",-18} max={maxDiff:0.######} mean={sumDiff / want.Length:0.######} allowed={allowed:0.######}");
        Assert.True(maxDiff <= allowed,
            $"{fixtureName} stage_tensor maxDiff {maxDiff:0.######} > allowed {allowed:0.######}");
    }

    private static void CompareStage(string fixtureName, string stage, byte[] actual, bool jpeg,
        ITestOutputHelper output)
    {
        var golden = NpyFile.Load(Path.Combine(GoldenDir, $"{Path.GetFileNameWithoutExtension(fixtureName)}.{stage}.npy"));
        Assert.Equal(3, golden.Shape[^1]);
        Assert.Equal(actual.Length, golden.Length);

        var want = golden.ToBytes();
        int maxDiff = 0;
        long sumDiff = 0;
        int changed = 0;
        int over50 = 0;
        for (int i = 0; i < want.Length; i++)
        {
            int diff = Math.Abs(want[i] - actual[i]);
            maxDiff = Math.Max(maxDiff, diff);
            sumDiff += diff;
            if (diff > 0) changed++;
            if (diff > 50) over50++;
        }
        int allowed = jpeg ? 128 : 0;
        Report(fixtureName, stage, maxDiff, sumDiff / (double)want.Length, allowed, output);
        output.WriteLine($"[golden] {fixtureName,-28} {stage,-18} pixelsChanged={changed * 100.0 / want.Length:0.###}% >50diff={over50 * 100.0 / want.Length:0.####}%");
        Assert.True(maxDiff <= allowed,
            $"{fixtureName} {stage} maxDiff {maxDiff} > allowed {allowed}");
    }

    private static void Report(string fixtureName, string stage, double maxDiff, double meanDiff, double allowed,
        ITestOutputHelper output)
    {
        output.WriteLine($"[golden] {fixtureName,-28} {stage,-18} max={maxDiff:0.######} mean={meanDiff:0.######} allowed={allowed}");
    }
}
