using System.Text.Json.Serialization;

namespace ImageTagger.ModelPackTool;

public sealed class ModelManifest
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";

    [JsonPropertyName("task")]
    public string Task { get; init; } = "";

    [JsonPropertyName("model")]
    public string Model { get; init; } = "";

    [JsonPropertyName("groups")]
    public GroupDescriptor[] Groups { get; init; } = [];

    [JsonPropertyName("input")]
    public InputContract Input { get; init; } = new();

    [JsonPropertyName("preprocessing")]
    public PreprocessingDescriptor Preprocessing { get; init; } = new();

    [JsonPropertyName("output")]
    public OutputContract Output { get; init; } = new();

    [JsonPropertyName("catalog")]
    public string Catalog { get; init; } = "";

    [JsonPropertyName("defaultThreshold")]
    public double DefaultThreshold { get; init; }

    [JsonPropertyName("build")]
    public BuildInfo? Build { get; init; }
}

public sealed class GroupDescriptor
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "";
}

public sealed class InputContract
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("layout")]
    public string Layout { get; init; } = "";

    [JsonPropertyName("dtype")]
    public string DType { get; init; } = "";

    [JsonPropertyName("batch")]
    public string Batch { get; init; } = "";

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}

public sealed class PreprocessingDescriptor
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    [JsonPropertyName("steps")]
    public StepDescriptor[] Steps { get; init; } = [];
}

public sealed class StepDescriptor
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = "";

    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Closed operator parameters are flattened beside op/version in the canonical manifest.</summary>
    [JsonExtensionData]
    public Dictionary<string, object?> Parameters { get; set; } = [];
}

public sealed class OutputContract
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("activation")]
    public string Activation { get; init; } = "";

    [JsonPropertyName("labelCount")]
    public int LabelCount { get; init; }
}

public sealed class BuildInfo
{
    [JsonPropertyName("tagSource")]
    public string TagSource { get; init; } = "";

    [JsonPropertyName("translationSource")]
    public string TranslationSource { get; init; } = "";

    [JsonPropertyName("categoryCounts")]
    public Dictionary<string, int> CategoryCounts { get; init; } = [];

    [JsonPropertyName("unmappedCategories")]
    public string[] UnmappedCategories { get; init; } = [];
}
