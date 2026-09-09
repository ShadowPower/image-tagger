namespace ImageTagger.Core.ModelPacks;

/// <summary>Ordered, versioned preprocessing steps from the manifest (design 3.4.1).</summary>
public sealed record PreprocessingPipelineDescriptor
{
    public required int SchemaVersion { get; init; }

    public required PreprocessStepDescriptor[] OrderedSteps { get; init; }
}

/// <summary>One atomic preprocessing step. Parameters are closed per-op schemas, never scripts.</summary>
public sealed record PreprocessStepDescriptor
{
    public required string Op { get; init; }

    public required int Version { get; init; }

    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// Model fingerprint inputs (design 14.5): file identity plus the normalized
/// preprocessing plan. Any change marks existing predictions stale.
/// </summary>
public sealed record ModelPackFingerprint
{
    public required string ModelFileName { get; init; }

    public required long ModelFileLength { get; init; }

    public required string ModelSha256 { get; init; }

    public required string CatalogSha256 { get; init; }

    public required string ManifestSha256 { get; init; }

    public required string PreprocessingFingerprint { get; init; }

    public string Value { get; init; } = "";
}
