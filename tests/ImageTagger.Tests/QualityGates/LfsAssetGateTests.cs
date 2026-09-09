using ImageTagger.Tests.Preprocessing;
using ImageTagger.Tests.TestData;
using ImageTagger.Tests.TestInfrastructure;
using Json.Schema;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Sdk;

namespace ImageTagger.Tests.QualityGates;

/// <summary>
/// Gates around LFS-backed assets: the real model must never be a pointer
/// file, and Model Pack manifests must validate against the embedded schema.
/// Missing LFS objects surface as explicit skips with remediation hints.
/// </summary>
public class LfsAssetGateTests
{
    private static string ModelPath =>
        Path.Combine(PreprocessingGoldenTests.RepoRoot, "Assets", "Models", "wd-eva02-tagger-2026-canary", "model.onnx");

    [Fact]
    public void Real_model_is_pulled_not_a_pointer()
    {
        LfsAssets.RequireRealFile(ModelPath, "real-model inference");
        Assert.True(new FileInfo(ModelPath).Length > 100_000_000,
            "model.onnx exists but looks truncated; run 'git lfs pull' again");
    }

    [Fact]
    public void Pointer_files_are_detected_and_skip_with_clear_message()
    {
        using var temp = new TempDirectory("lfs-pointer");
        var pointer = temp.WriteFile("fake.onnx",
            "version https://git-lfs.github.com/spec/v1\noid sha256:deadbeef\nsize 1\n"u8.ToArray());

        Assert.True(LfsAssets.IsGitLfsPointer(pointer));
        try
        {
            LfsAssets.RequireRealFile(pointer, "unit proof");
            Assert.Fail("pointer asset must be rejected");
        }
        catch (SkipException exception)
        {
            Assert.Contains("git lfs pull", exception.Message);
        }
    }

    [Fact]
    public void Real_binary_files_are_not_misdetected_as_pointers()
    {
        using var temp = new TempDirectory("lfs-real");
        var fixture = TestAssets.CopyFixture("photo_jpeg.jpg", temp.FullPath);
        Assert.False(LfsAssets.IsGitLfsPointer(fixture));
    }

    [Fact]
    public void Missing_files_skip_with_remediation_hint()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"imagetagger-nonexistent-{Guid.NewGuid():N}.onnx");
        try
        {
            LfsAssets.RequireRealFile(missing, "unit proof");
            Assert.Fail("missing asset must be rejected");
        }
        catch (SkipException exception)
        {
            Assert.Contains("git lfs pull", exception.Message);
        }
    }
}

/// <summary>
/// Validates every committed Model Pack manifest against the embedded schema.
/// Manifests are conventionally Assets/Models/&lt;pack&gt;/manifest.json; until
/// workflow B commits the first one this gate skips with an explanatory message.
/// </summary>
public class ModelPackManifestGateTests
{
    [Fact]
    public void All_committed_manifests_match_embedded_schema()
    {
        var root = Path.Combine(PreprocessingGoldenTests.RepoRoot, "Assets", "Models");
        var manifests = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories).ToList()
            : [];

        if (manifests.Count == 0)
            Assert.Skip("No Model Pack manifests (Assets/Models/*/manifest.json) committed yet; " +
                        "this gate activates automatically when workflow B lands them.");

        var schema = LoadEmbeddedSchema();
        var failures = new List<string>();
        foreach (var manifest in manifests)
        {
            if (LfsAssets.IsGitLfsPointer(manifest))
            {
                failures.Add($"{manifest} is an LFS pointer; run 'git lfs pull'");
                continue;
            }
            var node = JsonNode.Parse(File.ReadAllText(manifest));
            var result = schema.Evaluate(JsonSerializer.SerializeToElement(node!), new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            });
            if (!result.IsValid)
                failures.Add($"{manifest}: {string.Join("; ", CollectErrors(result))}");
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static JsonSchema LoadEmbeddedSchema()
    {
        var assembly = typeof(ImageTagger.Core.ModelPacks.ModelDescriptor).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("model-pack.schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry(),
        });
    }

    private static IEnumerable<string> CollectErrors(Json.Schema.EvaluationResults result)
    {
        if (result.Errors is { Count: > 0 })
            foreach (var error in result.Errors.Values)
                yield return $"{result.InstanceLocation}: {error}";
        if (result.Details is { Count: > 0 })
            foreach (var child in result.Details)
                foreach (var nested in CollectErrors(child))
                    yield return nested;
    }
}
