using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ImageTagger.Infrastructure.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Tests.Preprocessing;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

public sealed class ModelPackReaderTests
{
    [Fact]
    public async Task Loads_valid_descriptor_shared_catalog_and_stable_fingerprint()
    {
        using var temp = new TempDirectory("pack-valid");
        var root = CreatePack(temp);
        var reader = new ModelPackReader();

        var first = await reader.LoadAsync(root, TestContext.Current.CancellationToken);
        var second = await reader.LoadAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal("test-pack", first.Descriptor.Id);
        Assert.Equal("model.onnx", first.Descriptor.ModelFile);
        Assert.Equal("dynamic", first.Descriptor.Input.Batch);
        Assert.Equal(2, first.Catalog.Count);
        Assert.Equal("tag_one", first.Catalog[0].OriginalName);
        Assert.Equal("标签一", first.Catalog[0].ChineseTranslation);
        Assert.Null(first.Catalog[1].ChineseTranslation);
        Assert.Equal("subject", first.Catalog[1].DisplayGroup);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(64, first.Fingerprint.Value.Length);
        var resize = first.Descriptor.Preprocessing.OrderedSteps[1];
        Assert.Equal(64L, resize.Parameters["width"]);
        Assert.Equal("pillow-bicubic-v1", resize.Parameters["sampler"]);
    }

    [Fact]
    public async Task Rejects_hash_mismatch_without_using_other_directories()
    {
        using var temp = new TempDirectory("pack-hash");
        var root = CreatePack(temp);
        File.AppendAllText(Path.Combine(root, "model.onnx"), "changed");

        var exception = await Assert.ThrowsAsync<ModelPackValidationException>(
            () => new ModelPackReader().LoadAsync(root, TestContext.Current.CancellationToken));

        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("schema", "model.json 不符合")]
    [InlineData("path", "model.json 不符合")]
    [InlineData("label-count", "行数")]
    [InlineData("unknown-group", "未知分组")]
    [InlineData("duplicate-id", "contiguous")]
    public async Task Rejects_schema_path_and_catalog_semantic_errors(string mutation, string message)
    {
        using var temp = new TempDirectory($"pack-{mutation}");
        var root = CreatePack(temp);
        switch (mutation)
        {
            case "schema":
                MutateManifest(root, node => node["schemaVersion"] = 99);
                break;
            case "path":
                MutateManifest(root, node => node["model"] = "../model.onnx");
                break;
            case "label-count":
                MutateManifest(root, node => node["output"]!["labelCount"] = 3);
                break;
            case "unknown-group":
                File.WriteAllText(Path.Combine(root, "tags.csv"),
                    "id,name,group,translation,count\n0,tag_one,missing,标签一,1\n1,tag_two,subject,,2\n",
                    new UTF8Encoding(false));
                WriteChecksums(root);
                break;
            case "duplicate-id":
                File.WriteAllText(Path.Combine(root, "tags.csv"),
                    "id,name,group,translation,count\n0,tag_one,subject,标签一,1\n0,tag_two,subject,,2\n",
                    new UTF8Encoding(false));
                WriteChecksums(root);
                break;
        }

        var exception = await Assert.ThrowsAsync<ModelPackValidationException>(
            () => new ModelPackReader().LoadAsync(root, TestContext.Current.CancellationToken));

        Assert.Contains(message, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rejects_unknown_files_and_folder_id_mismatch()
    {
        using var temp = new TempDirectory("pack-layout");
        var root = CreatePack(temp);
        File.WriteAllText(Path.Combine(root, "training.txt"), "not runtime data");
        await Assert.ThrowsAsync<ModelPackValidationException>(
            () => new ModelPackReader().LoadAsync(root, TestContext.Current.CancellationToken));

        File.Delete(Path.Combine(root, "training.txt"));
        var renamed = Path.Combine(temp.FullPath, "wrong-folder");
        Directory.Move(root, renamed);
        await Assert.ThrowsAsync<ModelPackValidationException>(
            () => new ModelPackReader().LoadAsync(renamed, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rejects_symbolic_linked_pack_files_when_platform_supports_them()
    {
        using var temp = new TempDirectory("pack-symlink");
        var root = CreatePack(temp);
        var outside = temp.WriteFile("outside-model.onnx", [1, 2, 3, 4]);
        var model = Path.Combine(root, "model.onnx");
        File.Delete(model);
        try
        {
            File.CreateSymbolicLink(model, outside);
        }
        catch (Exception createException) when (createException is UnauthorizedAccessException
            or PlatformNotSupportedException or IOException)
        {
            return;
        }

        var validationException = await Assert.ThrowsAsync<ModelPackValidationException>(
            () => new ModelPackReader().LoadAsync(root, TestContext.Current.CancellationToken));
        Assert.Contains("符号链接", validationException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_is_observed_before_pack_io()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ModelPackReader().LoadAsync("not-used", cancellation.Token));
    }

    [Fact]
    [Trait("Category", "RealModel")]
    public async Task Built_in_pack_loads_with_all_catalog_entries()
    {
        var root = Path.Combine(
            PreprocessingGoldenTests.RepoRoot,
            "Assets", "Models", "wd-eva02-tagger-2026-canary");
        LfsAssets.RequireRealFile(Path.Combine(root, "model.onnx"), "built-in Model Pack reader");

        var pack = await new ModelPackReader().LoadAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(16_473, pack.Catalog.Count);
        Assert.Equal(16_473, pack.Descriptor.Output.LabelCount);
        Assert.Equal(["rating", "character", "general"], pack.Descriptor.Groups.Select(group => group.Id));
        var compiled = new PreprocessingPipelineCompiler().ValidateAndCompile(
            pack.Descriptor.Preprocessing,
            new ModelInputTensor
            {
                DType = TensorDType.Float32,
                Layout = pack.Descriptor.Input.Layout,
                Shape = [3, pack.Descriptor.Input.Height, pack.Descriptor.Input.Width],
            });
        Assert.Equal(11, compiled.Operators.Count);
    }

    internal static string CreatePack(TempDirectory temp)
    {
        var root = temp.CreateSubdirectory("test-pack");
        File.WriteAllBytes(Path.Combine(root, "model.onnx"), [1, 2, 3, 4]);
        File.WriteAllText(Path.Combine(root, "LICENSE.txt"), "test license\n", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "tags.csv"),
            "id,name,group,translation,count\n0,tag_one,subject,标签一,1\n1,tag_two,subject,,2\n",
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "model.json"), """
            {
              "schemaVersion": 1,
              "id": "test-pack",
              "displayName": "Test Pack",
              "task": "multi-label-image-tagging",
              "model": "model.onnx",
              "groups": [
                { "id": "subject", "name": "Subject", "displayName": "主体" }
              ],
              "input": { "name": "images", "layout": "NCHW", "dtype": "float32", "batch": "dynamic", "width": 64, "height": 64 },
              "preprocessing": {
                "schemaVersion": 1,
                "steps": [
                  { "op": "decode", "version": 1, "frame": "first", "colorManagement": "ignore" },
                  { "op": "resize", "version": 1, "width": 64, "height": 64, "sampler": "pillow-bicubic-v1" }
                ]
              },
              "output": { "name": "logits", "activation": "sigmoid", "labelCount": 2 },
              "catalog": "tags.csv",
              "defaultThreshold": 0.5
            }
            """, new UTF8Encoding(false));
        WriteChecksums(root);
        return root;
    }

    private static void MutateManifest(string root, Action<JsonObject> mutate)
    {
        var path = Path.Combine(root, "model.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        mutate(node);
        File.WriteAllText(path, node.ToJsonString(), new UTF8Encoding(false));
        WriteChecksums(root);
    }

    internal static void WriteChecksums(string root)
    {
        string[] names = ["model.onnx", "tags.csv", "model.json", "LICENSE.txt"];
        var lines = names.Select(name =>
            $"{Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, name))))}  {name}");
        File.WriteAllText(
            Path.Combine(root, "checksums.sha256"),
            string.Join('\n', lines) + "\n",
            new UTF8Encoding(false));
    }
}
