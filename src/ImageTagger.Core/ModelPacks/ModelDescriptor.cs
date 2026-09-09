namespace ImageTagger.Core.ModelPacks;

/// <summary>Runtime view of a validated Model Pack manifest (model.json, design 3.4).</summary>
public sealed record ModelDescriptor
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    /// <summary>Stable pack id (lower-case letters, digits, dashes); settings store only this.</summary>
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    /// <summary>Supported task constant; first version only accepts "multi-label-image-tagging".</summary>
    public required string Task { get; init; }

    /// <summary>Simple file name of the ONNX model inside the pack root.</summary>
    public required string ModelFile { get; init; }

    public required ModelInputContract Input { get; init; }

    /// <summary>Declared groups; array order is the tag tab display order and the reverse prompt order.</summary>
    public required GroupDescriptor[] Groups { get; init; }

    public required PreprocessingPipelineDescriptor Preprocessing { get; init; }

    public required ModelOutputContract Output { get; init; }

    /// <summary>Simple file name of the merged tag catalog (tags.csv).</summary>
    public required string Catalog { get; init; }

    public required double DefaultThreshold { get; init; }

    /// <summary>Optional resource/build metadata carried by the pack for provenance display.</summary>
    public IReadOnlyDictionary<string, string>? BuildInfo { get; init; }
}

/// <summary>A tag group declared by the pack; packs fully own their group taxonomy.</summary>
public sealed record GroupDescriptor(
    string Id,
    string Name,
    string DisplayName,
    string Strategy = "threshold");

public sealed record ModelInputContract(
    string Name,
    string Layout,
    string DType,
    string Batch,
    int Width,
    int Height);

public sealed record ModelOutputContract(
    string Name,
    /// <summary>"sigmoid" or "none".</summary>
    string Activation,
    int LabelCount);
