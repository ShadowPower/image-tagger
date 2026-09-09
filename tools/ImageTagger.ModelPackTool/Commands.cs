using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ImageTagger.ModelPackTool;

public static class ItModel
{
    public static void Create(string packRoot, string target)
    {
        using var stream = File.Create(target);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var name in PackFiles.All)
            zip.CreateEntryFromFile(Path.Combine(packRoot, name), name, CompressionLevel.Optimal);
    }

    /// <summary>Extracts to a fresh temp dir with entry guards; returns the dir (caller deletes).</summary>
    public static string ExtractToTempDir(string path)
    {
        var temp = Directory.CreateTempSubdirectory("itmodel-").FullName;
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (!seen.Add(entry.FullName))
                    throw new InvalidDataException($"duplicate archive entry: {entry.FullName}");
                if (entry.FullName.Contains("..") || Path.IsPathRooted(entry.FullName) ||
                    entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
                    throw new InvalidDataException($"unsafe archive entry: {entry.FullName}");
            }
            foreach (var name in PackFiles.All)
            {
                var entry = archive.GetEntry(name) ?? throw new InvalidDataException($"missing archive entry: {name}");
                entry.ExtractToFile(Path.Combine(temp, name), overwrite: false);
            }
            if (archive.Entries.Count != PackFiles.All.Length)
                throw new InvalidDataException(
                    $"archive must contain exactly {PackFiles.All.Length} entries, found {archive.Entries.Count}");
            return temp;
        }
        catch
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
            throw;
        }
    }
}

public static class PackCommand
{
    private const string DefaultId = "wd-eva02-tagger-2026-canary";
    private const string DefaultDisplayName = "WD EVA02 Tagger 2026 Canary";

    public static int Run(
        string source, string output, string? itmodel, string modelId, double threshold,
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

        if (itmodel is not null)
        {
            ItModel.Create(output, Path.GetFullPath(itmodel));
            Console.WriteLine($"wrote {itmodel}");
        }
        return 0;
    }
}

public static class ValidateCommand
{
    public static int Run(string pack)
    {
        string? tempDir = null;
        string root;
        try
        {
            if (File.Exists(pack) && pack.EndsWith(".itmodel", StringComparison.OrdinalIgnoreCase))
            {
                tempDir = ItModel.ExtractToTempDir(Path.GetFullPath(pack));
                root = tempDir;
            }
            else
            {
                root = Path.GetFullPath(pack);
                if (!Directory.Exists(root)) { Console.Error.WriteLine($"pack not found: {pack}"); return 1; }
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
        finally
        {
            if (tempDir is not null) Directory.Delete(tempDir, recursive: true);
        }
    }

    public static List<string> Validate(string root)
    {
        var errors = new List<string>();

        // 1. required files
        foreach (var name in PackFiles.All)
            if (!File.Exists(Path.Combine(root, name)))
                errors.Add($"missing file: {name}");
        if (errors.Count > 0) return errors;

        // 2. checksums
        var expected = Checksums.Read(root);
        foreach (var name in new[] { PackFiles.Model, PackFiles.Catalog, PackFiles.Manifest, PackFiles.License })
        {
            if (!expected.TryGetValue(name, out var want))
            {
                errors.Add($"checksums entry missing for: {name}");
                continue;
            }
            var got = Checksums.Sha256(Path.Combine(root, name));
            if (!string.Equals(got, want, StringComparison.Ordinal))
                errors.Add($"hash mismatch for {name}: expected {want[..12]}…, got {got[..12]}…");
        }
        if (errors.Count > 0) return errors;

        // 3. manifest
        var manifest = JsonSerializer.Deserialize(
            File.ReadAllText(Path.Combine(root, PackFiles.Manifest)),
            ModelPackJsonContext.Default.ModelManifest);
        if (manifest is null) { errors.Add("manifest unreadable"); return errors; }
        ValidateManifest(manifest, root, errors);

        // 4. catalog
        List<CatalogRow> rows;
        try
        {
            rows = CatalogCsv.Read(Path.Combine(root, PackFiles.Catalog));
        }
        catch (Exception ex)
        {
            errors.Add($"catalog unreadable: {ex.Message.Split('\n')[0]}");
            return errors;
        }
        if (manifest.Output.LabelCount != rows.Count)
            errors.Add($"labelCount {manifest.Output.LabelCount} != catalog rows {rows.Count}");
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Id != i)
            {
                errors.Add($"catalog ids must be contiguous from 0: index {i} has id {rows[i].Id}");
                break;
            }
        }
        var dupes = rows.GroupBy(r => r.Name).Where(g => g.Count() > 1).Select(g => g.Key).Take(3).ToList();
        if (dupes.Count > 0) errors.Add($"duplicate tag names: {string.Join(", ", dupes)}");

        var groupIds = manifest.Groups.Select(g => g.Id).ToHashSet();
        foreach (var r in rows)
            if (!groupIds.Contains(r.Group))
            {
                errors.Add($"unknown group '{r.Group}' at id {r.Id}");
                break;
            }

        return errors;
    }

    private static void ValidateManifest(ModelManifest m, string root, List<string> errors)
    {
        if (m.SchemaVersion != 1) errors.Add($"unsupported schemaVersion {m.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(m.Id)) errors.Add("manifest id empty");
        else if (m.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            errors.Add($"manifest id has invalid characters: {m.Id}");

        foreach (var (field, value) in new[] { ("model", m.Model), ("catalog", m.Catalog) })
            ValidateSimpleFileName(field, value, errors);

        if (m.Task != "multi-label-image-tagging") errors.Add($"unsupported task: {m.Task}");
        if (m.DefaultThreshold is < 0 or > 1) errors.Add($"defaultThreshold out of range: {m.DefaultThreshold}");
        if (m.Groups.Length == 0) errors.Add("manifest groups empty");
        if (m.Groups.Select(g => g.Id).Distinct().Count() != m.Groups.Length)
            errors.Add("duplicate group ids");
        foreach (var g in m.Groups)
            if (string.IsNullOrWhiteSpace(g.Id) || g.Id.Any(c => c is not (>= 'a' and <= 'z') and not '-'))
                errors.Add($"invalid group id: '{g.Id}'");

        if (m.Input.Layout != "NCHW") errors.Add($"unsupported input layout: {m.Input.Layout}");
        if (m.Input.DType != "float32") errors.Add($"unsupported input dtype: {m.Input.DType}");
        if (m.Input.Width <= 0 || m.Input.Height <= 0) errors.Add("input size must be positive");
        if (m.Output.Activation is not ("sigmoid" or "none")) errors.Add($"unsupported activation: {m.Output.Activation}");
        if (m.Output.LabelCount <= 0) errors.Add("output labelCount must be positive");

        // preprocessing: steps present, decode first and only once, bounded count
        var steps = m.Preprocessing.Steps;
        if (steps.Length == 0) errors.Add("preprocessing steps empty");
        if (steps.Length > 32) errors.Add($"too many preprocessing steps: {steps.Length}");
        if (steps.FirstOrDefault(s => s.Op == "decode") is { } first && first != steps[0])
            errors.Add("decode must be the first step");
        if (steps.Count(s => s.Op == "decode") > 1) errors.Add("decode must appear exactly once");
    }

    private static void ValidateSimpleFileName(string field, string value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.Contains("..")
            || Path.IsPathRooted(value))
            errors.Add($"{field} must be a simple file name inside the pack: '{value}'");
    }
}
