using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsvHelper;
using CsvHelper.Configuration;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using Json.Schema;

namespace ImageTagger.Infrastructure.ModelPacks;

/// <summary>A fully validated pack before preprocessing compilation and ONNX session creation.</summary>
public sealed record ValidatedModelPack(
    string Root,
    ModelDescriptor Descriptor,
    TagCatalog Catalog,
    ModelPackFingerprint Fingerprint);

public sealed class ModelPackValidationException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Reads only the explicitly supplied standard pack root, validates its closed
/// schema, files, hashes and catalog, and creates immutable shared runtime data.
/// </summary>
public sealed class ModelPackReader
{
    private static readonly string[] RequiredFiles =
        ["model.json", "model.onnx", "tags.csv", "checksums.sha256", "LICENSE.txt"];

    private static readonly string[] HashedFiles =
        ["model.onnx", "tags.csv", "model.json", "LICENSE.txt"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
    };

    private static readonly Lazy<JsonSchema> ManifestSchema = new(LoadManifestSchema);

    public async Task<ValidatedModelPack> LoadAsync(string packRoot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetFullPath(packRoot);
        ValidateRootAndFiles(root);

        var manifestPath = Path.Combine(root, "model.json");
        string manifestJson;
        try
        {
            manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelPackValidationException("无法读取 model.json。", exception);
        }

        ValidateSchema(manifestJson);
        ManifestDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(manifestJson, JsonOptions)
                ?? throw new JsonException("manifest is empty");
        }
        catch (JsonException exception)
        {
            throw new ModelPackValidationException($"model.json 无法解析：{exception.Message}", exception);
        }

        var descriptor = ToDescriptor(dto);
        if (!string.Equals(Path.GetFileName(root), descriptor.Id, StringComparison.Ordinal))
            throw new ModelPackValidationException("Model Pack 文件夹名必须与 manifest id 完全一致。");

        ValidateSemanticDescriptor(descriptor);
        var hashes = ReadChecksums(Path.Combine(root, "checksums.sha256"));
        foreach (var fileName in HashedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var stream = new FileStream(
                Path.Combine(root, fileName), FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexStringLower(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(hashes[fileName])))
            {
                throw new ModelPackValidationException($"{fileName} 的 SHA-256 校验失败。");
            }
        }

        var catalog = await Task.Run(
            () => ReadCatalog(Path.Combine(root, descriptor.Catalog), descriptor, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var manifestHash = hashes["model.json"];
        var preprocessingJson = JsonSerializer.Serialize(descriptor.Preprocessing, JsonOptions);
        var preprocessingHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(preprocessingJson)));
        var fingerprintValue = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', hashes["model.onnx"], hashes["tags.csv"], manifestHash, preprocessingHash))));

        return new ValidatedModelPack(
            root,
            descriptor,
            catalog,
            new ModelPackFingerprint
            {
                ModelFileName = descriptor.ModelFile,
                ModelFileLength = new FileInfo(Path.Combine(root, descriptor.ModelFile)).Length,
                ModelSha256 = hashes["model.onnx"],
                CatalogSha256 = hashes["tags.csv"],
                ManifestSha256 = manifestHash,
                PreprocessingFingerprint = preprocessingHash,
                Value = fingerprintValue,
            });
    }

    /// <summary>Reads and validates the manifest without hashing or opening ONNX.</summary>
    public ModelDescriptor ReadDescriptor(string packRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        var root = Path.GetFullPath(packRoot);
        ValidateRootAndFiles(root);
        var manifestPath = Path.Combine(root, "model.json");
        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelPackValidationException("无法读取 model.json。", exception);
        }

        ValidateSchema(json);
        try
        {
            var dto = JsonSerializer.Deserialize<ManifestDto>(json, JsonOptions)
                ?? throw new JsonException("manifest is empty");
            var descriptor = ToDescriptor(dto);
            ValidateSemanticDescriptor(descriptor);
            if (!string.Equals(Path.GetFileName(root), descriptor.Id, StringComparison.Ordinal))
                throw new ModelPackValidationException("Model Pack 文件夹名必须与 manifest id 完全一致。");
            return descriptor;
        }
        catch (ModelPackValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ModelPackValidationException($"model.json 无法解析：{exception.Message}", exception);
        }
    }

    private static void ValidateRootAndFiles(string root)
    {
        if (!Directory.Exists(root))
            throw new ModelPackValidationException("Model Pack 目录不存在。");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ModelPackValidationException("Model Pack 根目录不能是符号链接。");

        var expected = RequiredFiles.ToHashSet(StringComparer.Ordinal);
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelPackValidationException("无法枚举 Model Pack 目录。", exception);
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            if (!expected.Remove(name))
                throw new ModelPackValidationException($"Model Pack 包含未知条目：{name}");
            if (Directory.Exists(entry) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new ModelPackValidationException($"Model Pack 文件不能是目录或符号链接：{name}");
        }
        if (expected.Count > 0)
            throw new ModelPackValidationException($"Model Pack 缺少文件：{string.Join(", ", expected)}");
    }

    private static void ValidateSchema(string manifestJson)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(manifestJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        }
        catch (JsonException exception)
        {
            throw new ModelPackValidationException($"model.json 不是有效 JSON：{exception.Message}", exception);
        }
        using (document)
        {
            var result = ManifestSchema.Value.Evaluate(document.RootElement, new EvaluationOptions
            {
                OutputFormat = OutputFormat.List,
                RequireFormatValidation = true,
            });
            if (!result.IsValid)
                throw new ModelPackValidationException("model.json 不符合受支持的 schemaVersion 1。" );
        }
    }

    private static Dictionary<string, string> ReadChecksums(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0].Length != 64 || parts[0].Any(character => !char.IsAsciiHexDigit(character)))
                    throw new ModelPackValidationException("checksums.sha256 包含无效行。");
                if (!HashedFiles.Contains(parts[1], StringComparer.Ordinal) || !result.TryAdd(parts[1], parts[0].ToLowerInvariant()))
                    throw new ModelPackValidationException("checksums.sha256 包含未知或重复文件。");
            }
        }
        catch (ModelPackValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ModelPackValidationException("无法读取 checksums.sha256。", exception);
        }

        if (result.Count != HashedFiles.Length)
            throw new ModelPackValidationException("checksums.sha256 未覆盖全部必需文件。");
        return result;
    }

    private static TagCatalog ReadCatalog(
        string path,
        ModelDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var entries = new List<TagCatalogEntry>(descriptor.Output.LabelCount);
        var groups = descriptor.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            using var csv = new CsvReader(reader, new CsvConfiguration(System.Globalization.CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                MissingFieldFound = null,
                HeaderValidated = null,
                DetectDelimiter = false,
                TrimOptions = TrimOptions.None,
            });
            if (!csv.Read() || !csv.ReadHeader())
                throw new ModelPackValidationException("tags.csv 缺少表头。");
            string[] requiredHeaders = ["id", "name", "group", "translation", "count"];
            if (csv.HeaderRecord is null || !csv.HeaderRecord.SequenceEqual(requiredHeaders, StringComparer.Ordinal))
                throw new ModelPackValidationException("tags.csv 表头必须严格为 id,name,group,translation,count。");

            while (csv.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = csv.GetField<int>("id");
                var name = csv.GetField("name") ?? string.Empty;
                var group = csv.GetField("group") ?? string.Empty;
                var translation = csv.GetField("translation");
                _ = csv.GetField<long>("count");
                if (string.IsNullOrEmpty(name))
                    throw new ModelPackValidationException($"tags.csv 的 id {id} 标签名为空。");
                if (!groups.Contains(group))
                    throw new ModelPackValidationException($"tags.csv 的 id {id} 引用了未知分组 {group}。");
                entries.Add(new TagCatalogEntry(
                    id,
                    name,
                    string.IsNullOrEmpty(translation) || string.Equals(translation, name, StringComparison.Ordinal)
                        ? null
                        : translation,
                    group));
                if (entries.Count > descriptor.Output.LabelCount)
                    throw new ModelPackValidationException("tags.csv 行数超过 output.labelCount。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ModelPackValidationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or CsvHelperException or FormatException or OverflowException or ArgumentException)
        {
            throw new ModelPackValidationException($"tags.csv 无法解析：{exception.Message}", exception);
        }

        if (entries.Count != descriptor.Output.LabelCount)
            throw new ModelPackValidationException(
                $"tags.csv 行数 {entries.Count} 与 output.labelCount {descriptor.Output.LabelCount} 不一致。");
        try
        {
            return new TagCatalog(entries);
        }
        catch (ArgumentException exception)
        {
            throw new ModelPackValidationException($"tags.csv 语义无效：{exception.Message}", exception);
        }
    }

    private static ModelDescriptor ToDescriptor(ManifestDto dto) => new()
    {
        SchemaVersion = dto.SchemaVersion,
        Id = dto.Id,
        DisplayName = dto.DisplayName,
        Task = dto.Task,
        ModelFile = dto.Model,
        Groups = dto.Groups.Select(group =>
            new ImageTagger.Core.ModelPacks.GroupDescriptor(group.Id, group.Name, group.DisplayName,
                string.IsNullOrWhiteSpace(group.Strategy) ? "threshold" : group.Strategy)).ToArray(),
        Input = new ModelInputContract(
            dto.Input.Name,
            dto.Input.Layout,
            dto.Input.DType,
            dto.Input.Batch.ValueKind == JsonValueKind.Number ? dto.Input.Batch.GetRawText() : dto.Input.Batch.GetString()!,
            dto.Input.Width,
            dto.Input.Height),
        Preprocessing = new PreprocessingPipelineDescriptor
        {
            SchemaVersion = dto.Preprocessing.SchemaVersion,
            OrderedSteps = dto.Preprocessing.Steps.Select(step => new PreprocessStepDescriptor
            {
                Op = step.Op,
                Version = step.Version,
                Parameters = step.Parameters.ToDictionary(
                    pair => pair.Key,
                    pair => ConvertJsonValue(pair.Value),
                    StringComparer.Ordinal),
            }).ToArray(),
        },
        Output = new ModelOutputContract(
            dto.Output.Name, dto.Output.Activation, dto.Output.LabelCount),
        Catalog = dto.Catalog,
        DefaultThreshold = dto.DefaultThreshold,
        BuildInfo = dto.Build?.ToDictionary(
            pair => pair.Key, pair => pair.Value.GetRawText(), StringComparer.Ordinal),
    };

    private static void ValidateSemanticDescriptor(ModelDescriptor descriptor)
    {
        if (descriptor.Groups.Select(group => group.Id).Distinct(StringComparer.Ordinal).Count() != descriptor.Groups.Length)
            throw new ModelPackValidationException("manifest groups 包含重复 id。");
        if (descriptor.Groups.Any(group => group.Strategy is not ("threshold" or "rating")))
            throw new ModelPackValidationException("manifest groups.strategy 不受支持。");
        if (descriptor.Input.Batch is not ("dynamic" or "1"))
            throw new ModelPackValidationException("manifest input.batch 不受支持。");
        if (descriptor.Preprocessing.OrderedSteps.Count(step => step.Op == "decode") != 1
            || descriptor.Preprocessing.OrderedSteps[0].Op != "decode")
            throw new ModelPackValidationException("预处理必须且只能以一个 decode 开始。");
    }

    private static object? ConvertJsonValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => value.EnumerateArray().Select(ConvertJsonValue).ToArray(),
        _ => throw new ModelPackValidationException("预处理参数只能是标量或数组。"),
    };

    private static JsonSchema LoadManifestSchema()
    {
        var assembly = typeof(ModelDescriptor).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith("model-pack.schema.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Embedded Model Pack schema is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry(),
        });
    }

    private sealed record ManifestDto
    {
        public int SchemaVersion { get; init; }
        public required string Id { get; init; }
        public required string DisplayName { get; init; }
        public required string Task { get; init; }
        public required string Model { get; init; }
        public required GroupDto[] Groups { get; init; }
        public required InputDto Input { get; init; }
        public required PreprocessingDto Preprocessing { get; init; }
        public required OutputDto Output { get; init; }
        public required string Catalog { get; init; }
        public double DefaultThreshold { get; init; }
        public Dictionary<string, JsonElement>? Build { get; init; }
    }

    private sealed record GroupDto(string Id, string Name, string DisplayName, string? Strategy);

    private sealed record InputDto(
        string Name,
        string Layout,
        [property: JsonPropertyName("dtype")] string DType,
        JsonElement Batch,
        int Width,
        int Height);

    private sealed record PreprocessingDto(int SchemaVersion, StepDto[] Steps);

    private sealed record StepDto
    {
        public required string Op { get; init; }
        public int Version { get; init; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement> Parameters { get; init; } = new(StringComparer.Ordinal);
    }

    private sealed record OutputDto(string Name, string Activation, int LabelCount);
}
