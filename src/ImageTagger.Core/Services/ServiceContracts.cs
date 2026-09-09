using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;

namespace ImageTagger.Core.Services;

/// <summary>Normalizes import paths, deduplicates and reads basic file info (design 13.5).</summary>
public interface IImageImportService
{
    /// <summary>Imports files and/or folders. Folders are scanned recursively only when asked.
    /// Returns newly imported documents and per-item failures; duplicates (same canonical path) are skipped.</summary>
    Task<ImageImportResult> ImportAsync(
        IReadOnlyList<string> paths, bool recursive, CancellationToken cancellationToken,
        IReadOnlySet<string>? allowLargeImages = null);

    /// <summary>Releases one path after an image is removed from the session.</summary>
    void Forget(string canonicalPath);

    /// <summary>Releases all paths after the session is cleared.</summary>
    void ClearKnownPaths();
}

public enum ImageImportFailureKind
{
    NotFound,
    AccessDenied,
    UnsupportedFormat,
    CorruptImage,
    PixelLimitExceeded,
    IoError,
}

/// <summary>A non-fatal failure for one requested or discovered import path.</summary>
public sealed record ImageImportFailure(
    string Path,
    string FileName,
    ImageImportFailureKind Kind,
    string Message,
    long? PixelCount = null);

/// <summary>One import operation; valid files retain input/discovery order.</summary>
public sealed record ImageImportResult(
    IReadOnlyList<ImageDocument> Images,
    IReadOnlyList<ImageImportFailure> Failures,
    int DuplicateCount);

/// <summary>Asynchronous thumbnail generation and caching; output is UI-agnostic pixel data.</summary>
public interface IThumbnailService
{
    /// <summary>Decodes a thumbnail (bounded size, honors EXIF orientation); null when the format cannot be decoded.</summary>
    Task<ThumbnailResult?> GetOrCreateAsync(ImageDocument image, CancellationToken cancellationToken);
}

/// <summary>Decoded thumbnail pixels for the list UI.</summary>
public sealed record ThumbnailResult(int Width, int Height, byte[] PixelsBgra);

/// <summary>Platform-managed resource roots; the app never assumes a working directory (design 11.1).</summary>
public interface IAppResourceLocator
{
    /// <summary>App settings directory (settings.json, prompt-settings.json).</summary>
    string SettingsRoot { get; }

    /// <summary>Rolling log directory.</summary>
    string LogsRoot { get; }

    /// <summary>Cache directory for provider tuning results and CoreML compilation caches.</summary>
    string CacheRoot { get; }
}

/// <summary>Discovered Model Pack with validation state, used by the settings model list.</summary>
public sealed record ModelPackInfo(
    ModelDescriptor Descriptor,
    bool IsBuiltIn,
    bool IsValid,
    string? ValidationError);

/// <summary>
/// Discovers, installs, validates, loads, selects and unloads standard Model
/// Packs (design 13.5). Implementations may split internals into classes but
/// this boundary stays single.
/// </summary>
public interface IModelPackService
{
    /// <summary>Loads one explicitly configured Model Pack directory.</summary>
    Task<LoadedModelPack> LoadFromPathAsync(string modelPackPath, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>Reads lightweight model information from an explicit directory.</summary>
    ModelDescriptor ReadDescriptorFromPath(string modelPackPath) => throw new NotSupportedException();

}

/// <summary>A fully loaded, validated Model Pack ready for inference.</summary>
public sealed record LoadedModelPack(
    ModelDescriptor Descriptor,
    TagCatalog Catalog,
    ModelPackFingerprint Fingerprint,
    CompiledPreprocessingPipeline Pipeline);

/// <summary>Adapts a model family: preprocessing binding, output activation and group policy (design 13.5).</summary>
public interface ITaggerModelAdapter
{
    /// <summary>Whether this adapter supports the descriptor's task/input/output contract.</summary>
    bool Supports(ModelDescriptor descriptor);

    /// <summary>Applies the declared output activation (sigmoid or none) to raw logits.</summary>
    void Activate(ReadOnlySpan<float> logits, Span<float> probabilities);
}

/// <summary>Creates the platform inference session, reporting the device actually used (design 14.2).</summary>
public interface IInferenceRuntimeFactory
{
    /// <summary>
    /// Creates one long-lived session honoring the acceleration preference with
    /// CPU always available as final fallback. Session creation must not run on
    /// the UI thread; implementers return the warm-up outcome in the handle.
    /// </summary>
    Task<IInferenceSessionHandle> CreateAsync(
        ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken);
}

/// <summary>Owns the single active session and returns immutable prediction snapshots (design 13.5).</summary>
public interface ITagInferenceService
{
    /// <summary>True while a loaded session matches the given fingerprint.</summary>
    bool IsLoaded(ModelPackFingerprint fingerprint);

    /// <summary>Ensures a session for the pack (loading/replacing as needed), then runs one image.</summary>
    Task<PredictionSnapshot> InferAsync(
        ImageDocument image, LoadedModelPack pack, CancellationToken cancellationToken);
}

/// <summary>Parses and normalizes AI-generation metadata, preserving raw values (design 9).</summary>
public interface IGenerationInfoParser
{
    /// <summary>Returns null when no supported metadata is found; partial results carry warnings.</summary>
    Task<GenerationInfo?> ParseAsync(ImageDocument image, CancellationToken cancellationToken);
}

/// <summary>Atomic load/save of application settings in settings.json (design 15).</summary>
public interface ISettingsStore
{
    AppSettings Load();

    /// <summary>Writes atomically (temp file + replace); corrupt files fall back to defaults.</summary>
    void Save(AppSettings settings);
}

/// <summary>
/// Atomic persistence for the single prompt rule set (prompt-settings.json,
/// design 10.2 / 15). Separate from <see cref="ISettingsStore"/> so resetting
/// prompt rules never touches application settings.
/// </summary>
public interface IPromptSettingsStore
{
    /// <summary>Returns defaults when the file is missing, unreadable or corrupt (a backup is kept).</summary>
    PromptSettings Load();

    /// <summary>Queues a debounced atomic write; <paramref name="flush"/> forces it immediately.</summary>
    void Save(PromptSettings settings, bool flush = false);
}

/// <summary>File dialogs, clipboard and file-manager reveal (design 13.5).</summary>
public interface IPlatformService
{
    /// <summary>Shows an image file picker; returns null on cancel. Results are canonical paths.</summary>
    Task<IReadOnlyList<string>?> PickImageFilesAsync(CancellationToken cancellationToken);

    /// <summary>Shows a folder picker; returns null on cancel.</summary>
    Task<string?> PickFolderAsync(CancellationToken cancellationToken);

    /// <summary>Shows a save dialog for <paramref name="suggestedFileName"/>; returns the confirmed path or null.</summary>
    Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken);

    Task SetClipboardTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Reveals the file in Explorer/Finder; false means the platform operation failed.</summary>
    Task<bool> RevealInFileManagerAsync(string path, CancellationToken cancellationToken);

    /// <summary>Opens a file with its OS-default application; false means no handler or a launch failure.</summary>
    Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken);
}
