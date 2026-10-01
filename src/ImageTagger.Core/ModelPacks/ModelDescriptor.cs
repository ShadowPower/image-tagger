namespace ImageTagger.Core.ModelPacks;

/// <summary>Runtime view of a validated Model Pack manifest (model.json, design 3.4).</summary>
public sealed record ModelDescriptor
{
    /// <summary>Lowest Model Pack schema version this app accepts (design 3.4).</summary>
    public const int MinimumSchemaVersion = 1;

    /// <summary>
    /// Highest Model Pack schema version this app accepts. Version 2 adds the generic
    /// <c>resize-fit</c>/<c>pad-to-size</c> preprocessing operators, the
    /// <c>pillow-bilinear-v1</c> sampler and optional per-group default thresholds;
    /// version 1 manifests stay fully supported and unchanged.
    /// </summary>
    public const int MaximumSchemaVersion = 2;

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
/// <param name="DefaultThreshold">
/// Optional pack-recommended threshold for this group (schemaVersion 2). When present it is
/// the per-group fallback used while the user keeps per-group thresholds enabled; user
/// overrides in prompt settings always win. Packs without the field keep the single
/// <see cref="ModelDescriptor.DefaultThreshold"/> behavior.
/// </param>
public sealed record GroupDescriptor(
    string Id,
    string Name,
    string DisplayName,
    string Strategy = "threshold",
    double? DefaultThreshold = null);

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
