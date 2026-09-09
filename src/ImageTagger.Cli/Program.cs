using System.Text.Json;
using ImageTagger.App.Services;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.ModelPacks;
using ImageTagger.ModelPackTool;
using Microsoft.Extensions.DependencyInjection;

return await Cli.MainAsync(args);

static class Cli
{
    private static TextWriter Output = Console.Out;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly CliJsonContext JsonContext = new(Json);

    public static async Task<int> MainAsync(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("model", StringComparison.OrdinalIgnoreCase))
            return ModelCommand(args[1..]);
        if (args.Length > 0 && args[0].Equals("tag", StringComparison.OrdinalIgnoreCase)) args = args[1..];
        return await TagAsync(args);
    }

    private static int ModelCommand(string[] args)
    {
        if (args.Length == 0) return Usage();
        return args[0].ToLowerInvariant() switch
        {
            "pack" => Pack(args),
            "unpack" => Unpack(args),
            "validate" => Validate(args),
            _ => Usage(),
        };
    }

    private static int Pack(string[] args)
    {
        string? source = Option(args, "--source"), output = Option(args, "--output");
        if (source is null || output is null) { Console.Error.WriteLine("usage: model pack --source <dir> --output <dir> [--itmodel <file>]"); return 2; }
        return PackCommand.Run(source, output, Option(args, "--itmodel"), Option(args, "--id") ?? "wd-eva02-tagger-2026-canary", double.TryParse(Option(args, "--threshold"), out var t) ? t : 0.6094, Option(args, "--translations"));
    }

    private static int Unpack(string[] args)
    {
        var archive = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var output = Option(args, "--output");
        if (archive is null || output is null) { Console.Error.WriteLine("usage: model unpack <archive.itmodel> --output <dir>"); return 2; }
        try
        {
            var root = Path.GetFullPath(output);
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) { Console.Error.WriteLine("output directory is not empty"); return 1; }
            var temp = ItModel.ExtractToTempDir(Path.GetFullPath(archive));
            try
            {
                Directory.CreateDirectory(root);
                foreach (var file in Directory.EnumerateFiles(temp)) File.Move(file, Path.Combine(root, Path.GetFileName(file)), overwrite: false);
            }
            finally { try { Directory.Delete(temp, true); } catch { } }
            Console.WriteLine($"unpacked {archive} to {root}"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"unpack failed: {ex.Message}"); return 1; }
    }

    private static int Validate(string[] args)
    {
        var pack = Option(args, "--pack");
        if (pack is null) { Console.Error.WriteLine("usage: model validate --pack <dir|.itmodel>"); return 2; }
        return ValidateCommand.Run(pack);
    }

    private static async Task<int> TagAsync(string[] args)
    {
        var jsonl = args.Any(a => a.Equals("--jsonl", StringComparison.OrdinalIgnoreCase));
        var recursive = args.Any(a => a.Equals("--recursive", StringComparison.OrdinalIgnoreCase));
        var modelPath = Option(args, "--model-pack");
        var thresholdText = Option(args, "--threshold");
        double? threshold = null;
        if (thresholdText is not null)
        {
            if (!double.TryParse(thresholdText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed is < 0 or > 1)
            { Console.Error.WriteLine("--threshold must be a number between 0 and 1"); return 2; }
            threshold = parsed;
        }
        var outputPath = Option(args, "--output");
        var inputs = args.Where(a => !a.StartsWith("--") && !a.Equals(thresholdText) && !a.Equals(modelPath) && !a.Equals(outputPath)).ToArray();
        if (inputs.Length == 0) { Console.Error.WriteLine("usage: imagetagger [tag] <image|directory>... [--recursive] [--model-pack <dir>] [--jsonl]"); return 2; }
        try
        {
            await using var services = CliComposition.Build();
            var importer = services.GetRequiredService<IImageImportService>();
            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler? cancelHandler = null;
            cancelHandler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            Console.CancelKeyPress += cancelHandler;
            string? tempOutput = null;
            if (outputPath is not null)
            {
                var full = Path.GetFullPath(outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                tempOutput = full + ".tmp-" + Guid.NewGuid().ToString("N");
                Output = new StreamWriter(new FileStream(tempOutput, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new System.Text.UTF8Encoding(false)) { AutoFlush = true };
            }
            var imported = await importer.ImportAsync(inputs, recursive, cancellation.Token);
            var models = (ModelPackService)services.GetRequiredService<IModelPackService>();
            var recognition = new RecognitionService(models, services.GetRequiredService<ITagInferenceService>(), services.GetRequiredService<ISettingsStore>());
            var pack = modelPath is null ? await recognition.EnsureModelAsync(null, cancellation.Token) : await models.LoadFromPathAsync(modelPath, cancellation.Token);
            await recognition.RecognizeAllAsync(imported.Images, pack, null, cancellation.Token);
            foreach (var image in imported.Images) WriteResult(image, pack, threshold ?? pack.Descriptor.DefaultThreshold, jsonl);
            foreach (var failure in imported.Failures) WriteFailure(failure, jsonl);
            var code = imported.Failures.Count == 0 && imported.Images.All(i => i.AnalysisState == AnalysisState.Succeeded) ? 0 : 1;
            if (outputPath is not null)
            {
                await Output.FlushAsync(); Output.Dispose(); Output = Console.Out;
                File.Move(tempOutput!, Path.GetFullPath(outputPath), overwrite: true);
            }
            Console.CancelKeyPress -= cancelHandler;
            return code;
        }
        catch (OperationCanceledException) { if (jsonl) Emit(JsonSerializer.Serialize(new CliCanceledResult(1, "fatal_error", new CliError("canceled", "操作已取消。")), JsonContext.CliCanceledResult)); else Console.Error.WriteLine("操作已取消。"); return 130; }
        catch (Exception ex) { if (jsonl) Emit(JsonSerializer.Serialize(new CliFatalErrorResult(1, "fatal_error", ex.Message), JsonContext.CliFatalErrorResult)); else Console.Error.WriteLine($"error: {ex.Message}"); return 2; }
    }

    private static void WriteResult(ImageDocument image, LoadedModelPack pack, double threshold, bool jsonl)
    {
        var prediction = image.Prediction;
        TagResult[] tags = prediction is null ? [] : pack.Catalog.Entries
            .Select((entry, index) => new TagResult(entry.Index, entry.OriginalName,
                entry.ChineseTranslation, entry.DisplayGroup, prediction.Probabilities[index]))
            .Where(tag => tag.Probability >= threshold)
            .OrderByDescending(tag => tag.Probability)
            .ThenBy(tag => tag.Index)
            .ToArray();
        var result = new CliImageResult(
            1,
            "image_result",
            image.AnalysisState.ToString().ToLowerInvariant(),
            image.CanonicalPath,
            image.FileName,
            image.Format,
            image.PixelWidth,
            image.PixelHeight,
            image.FileSize,
            threshold,
            new CliModelResult(
                pack.Descriptor.Id,
                pack.Fingerprint.Value,
                prediction?.Runtime,
                prediction?.ExecutionProvider,
                prediction?.BatchSize),
            tags,
            tags.Length,
            string.Join(", ", tags.Select(tag => tag.Name)),
            prediction?.Duration.TotalMilliseconds,
            image.LastError);
        if (jsonl) Emit(JsonSerializer.Serialize(result, JsonContext.CliImageResult)); else WriteHuman(image, pack, threshold, tags);
    }

    private static void WriteHuman(ImageDocument image, LoadedModelPack pack, double threshold, TagResult[] tags)
    {
        var width = 100;
        try { if (!Console.IsOutputRedirected) width = Math.Clamp(Console.WindowWidth, 60, 180); } catch { }
        var rule = new string('─', width);
        Emit(rule);
        Emit(image.FileName);
        Emit($"{image.Format.ToUpperInvariant()}  {image.PixelWidth}×{image.PixelHeight}  {FormatBytes(image.FileSize)}  │  {image.AnalysisState}  │  {image.Prediction?.Duration.TotalMilliseconds:F0} ms");
        Emit(Trim(image.CanonicalPath, width));
        Emit($"模型 {pack.Descriptor.Id}  │  阈值 {threshold:P2}  │  标签 {tags.Length}");
        if (tags.Length == 0) { Emit("\n未识别到达到阈值的标签。\n"); return; }
        Emit(string.Empty);
        if (width >= 96)
        {
            var nameWidth = Math.Max(22, (width - 32) / 2);
            var translationWidth = width - nameWidth - 32;
            Emit($"{"置信度".PadRight(9)}  {"分组".PadRight(11)}  {"Tag".PadRight(nameWidth)}  中文");
            Emit(new string('─', width));
            foreach (var tag in tags)
                Emit($"{tag.Probability,8:P2}  {Trim(tag.Group, 11).PadRight(11)}  {Trim(tag.Name, nameWidth).PadRight(nameWidth)}  {Trim(tag.Translation ?? "—", translationWidth)}");
        }
        else
        {
            foreach (var tag in tags)
                Emit($"{tag.Probability,8:P2}  {Trim(tag.Name, width - 24).PadRight(width - 24)}  {tag.Group}");
        }
        Emit(string.Empty);
        Emit("Prompt");
        Emit(Wrap(string.Join(", ", tags.Select(tag => tag.Name)), width));
        Emit(string.Empty);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F2} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F2} MiB",
        >= 1024L => $"{bytes / 1024d:F2} KiB",
        _ => $"{bytes} B",
    };

    private static string Trim(string value, int width) => value.Length <= width ? value : value[..Math.Max(1, width - 1)] + "…";
    private static string Wrap(string value, int width)
    {
        if (value.Length <= width) return value;
        var lines = new List<string>();
        while (value.Length > width) { var split = value.LastIndexOf(", ", width, StringComparison.Ordinal); if (split < 1) split = width; lines.Add(value[..split]); value = value[Math.Min(value.Length, split + (split < value.Length - 1 && value[split] == ',' ? 2 : 0))..]; }
        lines.Add(value); return string.Join(Environment.NewLine, lines);
    }

    private static void WriteFailure(ImageImportFailure f, bool jsonl) { if (jsonl) Emit(JsonSerializer.Serialize(new CliFailureResult(1, "image_result", "failed", f.Path, f.FileName, new CliError(f.Kind.ToString().ToLowerInvariant(), f.Message)), JsonContext.CliFailureResult)); else Console.Error.WriteLine($"{f.FileName}: {f.Message}"); }
    private static void Emit(string text) => Output.WriteLine(text);
    private static string? Option(string[] args, string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    private static int Usage() { Console.WriteLine("usage: imagetagger [tag] <inputs...> | model <pack|unpack|validate> ..."); return 2; }

}

internal sealed record CliError(string Code, string Message);
internal sealed record CliCanceledResult(int SchemaVersion, string Type, CliError Error);
internal sealed record CliFatalErrorResult(int SchemaVersion, string Type, string Error);
internal sealed record CliModelResult(string Id, string Fingerprint, string? Runtime, string? ExecutionProvider, int? BatchSize);
internal sealed record TagResult(int Index, string Name, string? Translation, string Group, float Probability);
internal sealed record CliImageResult(
    int SchemaVersion,
    string Type,
    string Status,
    string Path,
    string FileName,
    string Format,
    int Width,
    int Height,
    long FileSize,
    double Threshold,
    CliModelResult Model,
    TagResult[] Tags,
    int TagCount,
    string Prompt,
    double? DurationMs,
    string? Error);
internal sealed record CliFailureResult(
    int SchemaVersion,
    string Type,
    string Status,
    string Path,
    string FileName,
    CliError Error);

[System.Text.Json.Serialization.JsonSerializable(typeof(CliCanceledResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CliFatalErrorResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CliImageResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(CliFailureResult))]
internal sealed partial class CliJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
