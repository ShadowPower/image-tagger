namespace ImageTagger.Core.Domain;

/// <summary>
/// One image in the working session. Identity fields are fixed at import; state
/// fields are updated by the thumbnail, metadata and inference pipelines.
/// </summary>
public sealed class ImageDocument
{
    public required string Id { get; init; }

    /// <summary>Normalized absolute path resolved at import time; never logged in full.</summary>
    public required string CanonicalPath { get; init; }

    public required string FileName { get; init; }

    public required long FileSize { get; init; }

    /// <summary>Lower-case container format, e.g. "png", "jpeg".</summary>
    public required string Format { get; init; }

    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    public ThumbnailState ThumbnailState { get; set; } = ThumbnailState.NotStarted;

    public AnalysisState AnalysisState { get; set; } = AnalysisState.NotRun;

    /// <summary>Immutable result replaced wholesale after inference; never mutated in place.</summary>
    public PredictionSnapshot? Prediction { get; set; }

    /// <summary>Parsed AI-generation metadata; null until first shown as current image.</summary>
    public GenerationInfo? Generation { get; set; }

    /// <summary>User-facing failure summary for the last failed operation on this image.</summary>
    public string? LastError { get; set; }

    /// <summary>Set only after the user explicitly approves decoding a large image.</summary>
    public bool LargeImageApproved { get; set; }
}

/// <summary>Immutable inference result; probabilities align 1:1 with the TagCatalog of the model fingerprint.</summary>
public sealed record PredictionSnapshot(
    string ModelFingerprint,
    DateTimeOffset CreatedAt,
    TimeSpan Duration,
    string Runtime,
    string ExecutionProvider,
    int BatchSize,
    float[] Probabilities);
