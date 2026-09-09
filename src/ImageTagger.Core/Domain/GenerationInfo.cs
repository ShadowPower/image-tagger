namespace ImageTagger.Core.Domain;

/// <summary>Recognized generator of an image's embedded metadata (design 9.1).</summary>
public enum GenerationSource
{
    Unknown,
    Automatic1111,
    Forge,
    ComfyUi,
    NovelAi,
}

/// <summary>Resource referenced by generation metadata (checkpoint, VAE, LoRA, embedding).</summary>
public sealed record GenerationResource(
    GenerationResourceKind Kind,
    string Name,
    string? Hash,
    string? Weight);

public enum GenerationResourceKind
{
    Checkpoint,
    Vae,
    Lora,
    Embedding,
}

/// <summary>
/// Normalized AI-generation metadata. Every field is optional; unknown keys are
/// preserved in <see cref="RawEntries"/> so normalization never discards data.
/// </summary>
public sealed record GenerationInfo
{
    public required GenerationSource Source { get; init; }

    public string? SoftwareVersion { get; init; }

    public string? PositivePrompt { get; init; }

    public string? NegativePrompt { get; init; }

    /// <summary>Normalized scalar parameters keyed by canonical name (steps, sampler, cfg, seed, size, ...).</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<GenerationResource> Resources { get; init; } = [];

    /// <summary>Unrecognized original key/values and raw text, kept for display and diagnostics.</summary>
    public IReadOnlyDictionary<string, string> RawEntries { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Non-fatal parse warnings; damaged metadata yields partial results plus warnings.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
