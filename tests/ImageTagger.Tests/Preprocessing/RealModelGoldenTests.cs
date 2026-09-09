using System.Text.Json;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Tests.TestData;
using ImageTagger.Tests.TestInfrastructure;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace ImageTagger.Tests.Preprocessing;

/// <summary>
/// P-05 end-to-end baseline: C# preprocessing + ONNX Runtime CPU inference must
/// reproduce the Python reference probabilities and threshold-selected tag set
/// for the fixed baseline fixtures. Requires the real model pack; tagged RealModel.
/// </summary>
[Trait("Category", "RealModel")]
public class RealModelGoldenTests
{
    private readonly ITestOutputHelper _output;

    public RealModelGoldenTests(ITestOutputHelper output) => _output = output;

    private const float Threshold = 0.6094f;
    private static readonly string[] Baselines = ["landscape_rgb.png", "rgba_alpha.png", "photo_jpeg.jpg"];

    private static string ModelPath =>
        Path.Combine(PreprocessingGoldenTests.RepoRoot, "Assets", "Models", "wd-eva02-tagger-2026-canary", "model.onnx");

    private static string TagsPath =>
        Path.Combine(PreprocessingGoldenTests.RepoRoot, "Assets", "Models", "wd-eva02-tagger-2026-canary", "tags.csv");

    public static TheoryData<string> BaselineNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in Baselines) names.Add(name);
        return names;
    }

    [Theory]
    [MemberData(nameof(BaselineNames))]
    public void End_to_end_labels_match_python_baseline(string fixtureName)
    {
        LfsAssets.RequireRealFile(ModelPath, "real-model golden baseline");
        var stages = PillowReferencePreprocessor.Run(Path.Combine(PreprocessingGoldenTests.FixturesDir, fixtureName));
        var (probs, tagNames) = RunInference(stages.Tensor, stages.Size);

        var goldenProbs = NpyFile.Load(Path.Combine(
            PreprocessingGoldenTests.GoldenDir, $"{Path.GetFileNameWithoutExtension(fixtureName)}.probs.npy")).ToFloats();
        Assert.Equal(goldenProbs.Length, probs.Length);

        float maxDiff = 0;
        int argmaxGolden = 0, argmaxActual = 0;
        for (int i = 0; i < probs.Length; i++)
        {
            float diff = Math.Abs(goldenProbs[i] - probs[i]);
            maxDiff = Math.Max(maxDiff, diff);
            if (goldenProbs[i] > goldenProbs[argmaxGolden]) argmaxGolden = i;
            if (probs[i] > probs[argmaxActual]) argmaxActual = i;
        }
        _output.WriteLine($"[real-model] {fixtureName} probs maxDiff={maxDiff:0.####E+00} " +
                          $"top1 golden={tagNames[argmaxGolden]}({goldenProbs[argmaxGolden]:0.####}) " +
                          $"actual={tagNames[argmaxActual]}({probs[argmaxActual]:0.####})");
        // Lossless fixtures reproduce Pillow bit-exactly, so any diff here is the
        // JPEG decoder gap quantized in ADR 0002. Recorded, not asserted.

        var baseline = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(
            PreprocessingGoldenTests.GoldenDir, $"{Path.GetFileNameWithoutExtension(fixtureName)}.baseline.json")));

        var expectedTop50 = baseline.GetProperty("top50").EnumerateArray().ToList();
        Assert.True(expectedTop50.Count == 50);
        // Top-10 of the golden must all appear in the C# top-10 as sanity ordering check.
        var actualOrder = Enumerable.Range(0, probs.Length).OrderByDescending(i => probs[i]).Take(50).ToList();
        var goldenOrder = expectedTop50.Select(t => t.GetProperty("id").GetInt32()).ToList();
        int overlap = goldenOrder.Take(10).Intersect(actualOrder.Take(10)).Count();
        _output.WriteLine($"[real-model] {fixtureName} top10 overlap={overlap}/10");
        Assert.True(overlap >= 9, $"{fixtureName} top10 overlap {overlap}/10 < 9");

        int expectedCount = baseline.GetProperty("selectedCount").GetInt32();
        string expectedSha = baseline.GetProperty("selectedIdsSha256").GetString()!;
        var selected = Enumerable.Range(0, probs.Length).Where(i => probs[i] >= Threshold)
            .OrderBy(i => i).ToList();
        _output.WriteLine($"[real-model] {fixtureName} selected actual={selected.Count} expected={expectedCount}");
        Assert.Equal(expectedCount, selected.Count);
        Assert.Equal(expectedSha, Sha256OfSelection(selected, tagNames));
    }

    private static (float[] Probs, string[] TagNames) RunInference(float[] tensor, int size)
    {
        var tagNames = File.ReadLines(TagsPath).Skip(1)
            .Select(l => l.Split(',')[1])
            .ToArray();

        using var session = new InferenceSession(ModelPath, new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        });
        var inputName = session.InputMetadata.ContainsKey("images")
            ? "images"
            : session.InputMetadata.Keys.First();
        var input = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(
            tensor, [1, 3, size, size]);
        using var outputs = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
        var logits = outputs.First().AsEnumerable<float>().ToArray();
        var probs = new float[logits.Length];
        for (int i = 0; i < logits.Length; i++)
            probs[i] = 1f / (1f + MathF.Exp(-logits[i]));
        return (probs, tagNames);
    }

    private static string Sha256OfSelection(List<int> selected, string[] tagNames)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var payload = string.Join(",", selected.Select(i => $"{i}:{tagNames[i]}"));
        return Convert.ToHexStringLower(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(payload)));
    }
}
