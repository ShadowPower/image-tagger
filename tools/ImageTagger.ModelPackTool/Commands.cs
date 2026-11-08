using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageTagger.ModelPackTool;

public static class PackCommand
{
    private const string DefaultId = "wd-eva02-tagger-2026-canary";
    private const string DefaultDisplayName = "WD EVA02 Tagger 2026 Canary";

    public static int Run(
        string source, string output, string modelId, double threshold,
        string? translationsFile = null)
    {
        source = Path.GetFullPath(source);
        output = Path.GetFullPath(output);
        if (!Directory.Exists(source)) { Console.Error.WriteLine($"source not found: {source}"); return 1; }

        var translationsPath = translationsFile is null
            ? Path.Combine(source, "translated_tags_zh.jsonl")
            : Path.GetFullPath(translationsFile);
        if (!File.Exists(translationsPath)) { Console.Error.WriteLine($"translations not found: {translationsPath}"); return 1; }

        var tags = TagMerge.ReadSourceTags(Path.Combine(source, "selected_tags.csv"));
        var translations = TagMerge.ReadTranslations(translationsPath);
        var (rows, groupOrder, build) = TagMerge.Build(tags, translations, Path.GetFileName(translationsPath));

        Directory.CreateDirectory(output);
        foreach (var f in Directory.EnumerateFiles(output))
            File.Delete(f);

        File.Copy(Path.Combine(source, "onnx", "model_int8_quality.onnx"),
            Path.Combine(output, PackFiles.Model), overwrite: true);
        CatalogCsv.Write(Path.Combine(output, PackFiles.Catalog), rows);

        // Group order follows model output order; fall back to canonical display order for stability.
        var orderedGroups = groupOrder
            .OrderBy(g => Array.IndexOf(GroupMap.DisplayOrder, g) is var i && i >= 0 ? i : 99)
            .Select(GroupMap.Describe)
            .ToArray();

        var manifest = new ModelManifest
        {
            Id = modelId,
            DisplayName = DefaultDisplayName,
            Task = "multi-label-image-tagging",
            Model = PackFiles.Model,
            Groups = orderedGroups,
            Input = new InputContract
            {
                Name = "images",
                Layout = "NCHW",
                DType = "float32",
                Batch = "dynamic",
                Width = 448,
                Height = 448,
            },
            Preprocessing = new PreprocessingDescriptor
            {
                Steps =
                [
                    new StepDescriptor
                    {
                        Op = "decode",
                        Parameters = new() { ["frame"] = "first", ["colorManagement"] = "ignore" },
                    },
                    new StepDescriptor { Op = "exif-transpose" },
                    new StepDescriptor
                    {
                        Op = "ensure-color",
                        Parameters = new() { ["mode"] = "rgb-or-rgba-if-transparent" },
                    },
                    new StepDescriptor
                    {
                        Op = "alpha-composite",
                        Parameters = new() { ["when"] = "has-alpha", ["background"] = new object?[] { 255L, 255L, 255L } },
                    },
                    new StepDescriptor
                    {
                        Op = "pad-to-square",
                        Parameters = new() { ["anchor"] = "floor-center", ["background"] = new object?[] { 255L, 255L, 255L } },
                    },
                    new StepDescriptor
                    {
                        Op = "resize",
                        Parameters = new()
                        {
                            ["width"] = 448L,
                            ["height"] = 448L,
                            ["sampler"] = "pillow-bicubic-v1",
                        },
                    },
                    new StepDescriptor
                    {
                        Op = "reorder-channels",
                        Parameters = new() { ["order"] = new object?[] { 2L, 1L, 0L } },
                    },
                    new StepDescriptor { Op = "cast", Parameters = new() { ["dtype"] = "float32" } },
                    new StepDescriptor { Op = "divide", Parameters = new() { ["value"] = 255.0 } },
                    new StepDescriptor
                    {
                        Op = "normalize",
                        Parameters = new()
                        {
                            ["mean"] = new object?[] { 0.5, 0.5, 0.5 },
                            ["std"] = new object?[] { 0.5, 0.5, 0.5 },
                        },
                    },
                    new StepDescriptor
                    {
                        Op = "permute",
                        Parameters = new() { ["order"] = new object?[] { 2L, 0L, 1L } },
                    },
                ],
            },
            Output = new OutputContract
            {
                Name = "logits",
                Activation = "sigmoid",
                LabelCount = rows.Count,
            },
            Catalog = PackFiles.Catalog,
            DefaultThreshold = threshold,
            Build = build,
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var json = JsonSerializer.Serialize(
            manifest,
            new ModelPackJsonContext(jsonOptions).ModelManifest);
        File.WriteAllText(Path.Combine(output, PackFiles.Manifest), json.Replace("\r\n", "\n") + "\n", new UTF8Encoding(false));

        File.Copy(Path.Combine(source, "LICENSE"), Path.Combine(output, PackFiles.License), overwrite: true);
        Checksums.Write(output);

        Console.WriteLine($"packed {rows.Count} labels into {output}");
        foreach (var g in orderedGroups)
            Console.WriteLine($"  group {g.Id}: {rows.Count(r => r.Group == g.Id)} labels");

        return 0;
    }
}

public static class ValidateCommand
{
    public static int Run(string pack)
    {
        var root = Path.GetFullPath(pack);
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"pack not found: {pack}");
            return 1;
        }

        var errors = Validate(root);
        if (errors.Count == 0)
        {
            Console.WriteLine($"valid: {pack}");
            return 0;
        }
        foreach (var e in errors) Console.Error.WriteLine($"error: {e}");
        return 1;
    }

    public static List<string> Validate(string root)
    {
        // 运行时读取器（ModelPackReader）是 manifest schema、文件哈希、目录布局与
        // catalog 语义的唯一权威。开发工具不再维护第二套近似校验，否则会与
        // schemaVersion 2 等新契约漂移（例如 input.batch 允许数字 1）。
        try
        {
            _ = new ImageTagger.Infrastructure.ModelPacks.ModelPackReader()
                .LoadAsync(Path.GetFullPath(root), CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return [];
        }
        catch (ImageTagger.Infrastructure.ModelPacks.ModelPackValidationException exception)
        {
            return [exception.Message];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [exception.Message];
        }
    }
}
