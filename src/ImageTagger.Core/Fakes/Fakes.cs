using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;

namespace ImageTagger.Core.Fakes;

/// <summary>
/// Builds a tiny in-memory Model Pack descriptor for tests: 6 labels across two
/// groups and a 1x1-input pipeline description. Proves the generic layer is not
/// bound to WD constants.
/// </summary>
public static class FakeModelPack
{
    public const string Id = "fake-micro-tagger";

    public static ModelDescriptor Descriptor(
        string id = Id,
        int width = 64,
        int height = 64,
        int labelCount = 6,
        string activation = "sigmoid") => new()
        {
            SchemaVersion = 1,
            Id = id,
            DisplayName = "Fake Micro Tagger",
            Task = "multi-label-image-tagging",
            ModelFile = "model.onnx",
            Groups =
        [
            new GroupDescriptor("subject", "Subject", "主体"),
            new GroupDescriptor("style", "Style", "风格"),
        ],
            Input = new ModelInputContract("images", "NCHW", "float32", "dynamic", width, height),
            Preprocessing = new PreprocessingPipelineDescriptor
            {
                SchemaVersion = 1,
                OrderedSteps =
            [
                new PreprocessStepDescriptor { Op = "decode", Version = 1, Parameters = DefaultParameters.Decode },
                new PreprocessStepDescriptor { Op = "exif-transpose", Version = 1 },
                new PreprocessStepDescriptor { Op = "ensure-color", Version = 1, Parameters = DefaultParameters.EnsureColor },
                new PreprocessStepDescriptor { Op = "alpha-composite", Version = 1, Parameters = DefaultParameters.Composite },
                new PreprocessStepDescriptor { Op = "pad-to-square", Version = 1, Parameters = DefaultParameters.Pad },
                new PreprocessStepDescriptor
                {
                    Op = "resize", Version = 1,
                    Parameters = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["width"] = width,
                        ["height"] = height,
                        ["sampler"] = "pillow-bicubic-v1",
                    },
                },
                new PreprocessStepDescriptor { Op = "reorder-channels", Version = 1, Parameters = DefaultParameters.Reorder },
                new PreprocessStepDescriptor { Op = "cast", Version = 1, Parameters = DefaultParameters.Cast },
                new PreprocessStepDescriptor { Op = "divide", Version = 1, Parameters = DefaultParameters.Divide },
                new PreprocessStepDescriptor { Op = "normalize", Version = 1, Parameters = DefaultParameters.Normalize },
                new PreprocessStepDescriptor { Op = "permute", Version = 1, Parameters = DefaultParameters.Permute },
            ],
            },
            Output = new ModelOutputContract("logits", activation, labelCount),
            Catalog = "tags.csv",
            DefaultThreshold = 0.5,
        };

    public static TagCatalog Catalog(int labelCount = 6) => new(
    [
        new TagCatalogEntry(0, "sunny", "晴天", "subject"),
        new TagCatalogEntry(1, "night", "夜晚", "subject"),
        new TagCatalogEntry(2, "portrait", "肖像", "subject"),
        new TagCatalogEntry(3, "watercolor", null, "style"),
        new TagCatalogEntry(4, "photo", "照片", "style"),
        new TagCatalogEntry(5, "sketch", "素描", "style"),
    ]);

    public static ModelPackFingerprint Fingerprint(string suffix = "test") => new()
    {
        ModelFileName = "model.onnx",
        ModelFileLength = 42,
        ModelSha256 = new string('a', 64),
        CatalogSha256 = new string('b', 64),
        ManifestSha256 = new string('c', 64),
        PreprocessingFingerprint = "fake-preproc-1",
        Value = $"fake-{suffix}",
    };

    /// <summary>Parameter dictionaries reused verbatim from the WD-shaped step set.</summary>
    private static class DefaultParameters
    {
        public static readonly IReadOnlyDictionary<string, object?> Decode =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["frame"] = "first",
                ["colorManagement"] = "ignore",
            };
        public static readonly IReadOnlyDictionary<string, object?> EnsureColor =
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["mode"] = "rgb-or-rgba-if-transparent" };
        public static readonly IReadOnlyDictionary<string, object?> Composite =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["when"] = "has-alpha",
                ["background"] = new object?[] { 255L, 255L, 255L },
            };
        public static readonly IReadOnlyDictionary<string, object?> Pad =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["anchor"] = "floor-center",
                ["background"] = new object?[] { 255L, 255L, 255L },
            };
        public static readonly IReadOnlyDictionary<string, object?> Reorder =
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = new object?[] { 2L, 1L, 0L } };
        public static readonly IReadOnlyDictionary<string, object?> Cast =
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["dtype"] = "float32" };
        public static readonly IReadOnlyDictionary<string, object?> Divide =
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["value"] = 255.0 };
        public static readonly IReadOnlyDictionary<string, object?> Normalize =
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["mean"] = new object?[] { 0.5, 0.5, 0.5 },
                ["std"] = new object?[] { 0.5, 0.5, 0.5 },
            };
        public static readonly IReadOnlyDictionary<string, object?> Permute =
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["order"] = new object?[] { 2L, 0L, 1L } };
    }
}

/// <summary>Always-successful inference service returning deterministic probabilities.</summary>
public sealed class FakeTagInferenceService : ITagInferenceService
{
    private readonly Func<ImageDocument, float[]> _probabilities;

    public FakeTagInferenceService(Func<ImageDocument, float[]>? probabilities = null)
    {
        _probabilities = probabilities ?? (image =>
        {
            var random = new Random(image.Id.GetHashCode());
            var probs = new float[FakeModelPack.Catalog().Count];
            for (int i = 0; i < probs.Length; i++)
                probs[i] = (float)random.NextDouble();
            return probs;
        });
    }

    public bool IsLoaded(ModelPackFingerprint fingerprint) => true;

    private int _inferCalls;

    public int InferCalls => _inferCalls;

    public Task<PredictionSnapshot> InferAsync(
        ImageDocument image, LoadedModelPack pack, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _inferCalls);
        var probs = _probabilities(image);
        return Task.FromResult(new PredictionSnapshot(
            pack.Fingerprint.Value,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(5),
            "fake-runtime",
            "cpu",
            1,
            probs));
    }
}

/// <summary>In-memory platform service: preset picker results and a text clipboard.</summary>
public sealed class FakePlatformService : IPlatformService
{
    public IReadOnlyList<string>? NextPickedFiles { get; set; }

    public string? NextPickedFolder { get; set; }

    public string? NextSavePath { get; set; }

    public string? ClipboardText { get; private set; }

    public string? LastRevealedPath { get; private set; }

    public string? LastOpenedPath { get; private set; }

    public bool ExternalLaunchSucceeds { get; set; } = true;

    public Task<IReadOnlyList<string>?> PickImageFilesAsync(CancellationToken cancellationToken) =>
        Task.FromResult(NextPickedFiles);

    public Task<string?> PickFolderAsync(CancellationToken cancellationToken) =>
        Task.FromResult(NextPickedFolder);

    public Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken) =>
        Task.FromResult(NextSavePath);

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        ClipboardText = text;
        return Task.CompletedTask;
    }

    public Task<bool> RevealInFileManagerAsync(string path, CancellationToken cancellationToken)
    {
        LastRevealedPath = path;
        return Task.FromResult(ExternalLaunchSucceeds);
    }

    public Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        LastOpenedPath = path;
        return Task.FromResult(ExternalLaunchSucceeds);
    }
}

/// <summary>Resource roots under a single temp base; no machine paths leak into consumers.</summary>
public sealed class FakeAppResourceLocator : IAppResourceLocator
{
    public FakeAppResourceLocator(string baseRoot)
    {
        BuiltInModelsRoot = Path.Combine(baseRoot, "builtin-models");
        ManagedModelsRoot = Path.Combine(baseRoot, "managed-models");
        SettingsRoot = Path.Combine(baseRoot, "settings");
        LogsRoot = Path.Combine(baseRoot, "logs");
        CacheRoot = Path.Combine(baseRoot, "cache");
    }

    public string BuiltInModelsRoot { get; }

    public string ManagedModelsRoot { get; }

    public string SettingsRoot { get; }

    public string LogsRoot { get; }

    public string CacheRoot { get; }
}

/// <summary>Deterministic import service for tests that synthesizes documents.</summary>
public sealed class FakeImageImportService : IImageImportService
{
    private readonly List<ImageDocument> _documents = [];

    public FakeImageImportService(params ImageDocument[] documents) => _documents.AddRange(documents);

    public Task<ImageImportResult> ImportAsync(
        IReadOnlyList<string> paths, bool recursive, CancellationToken cancellationToken,
        IReadOnlySet<string>? allowLargeImages = null)
        => Task.FromResult(new ImageImportResult(_documents, [], 0));

    public void Forget(string canonicalPath)
    {
    }

    public void ClearKnownPaths()
    {
    }
}

/// <summary>Design-time sample data for the designer preview and UI tests.</summary>
public static class DesignData
{
    public static ImageDocument MakeImage(
        string name = "sunset_beach.png",
        int width = 832,
        int height = 1216,
        AnalysisState state = AnalysisState.Succeeded) => new()
        {
            Id = Guid.NewGuid().ToString("N"),
            CanonicalPath = Path.Combine("%IMAGES%", name),
            FileName = name,
            FileSize = 1_234_567,
            Format = "png",
            PixelWidth = width,
            PixelHeight = height,
            ThumbnailState = ThumbnailState.Loaded,
            AnalysisState = state,
            Prediction = new PredictionSnapshot(
            FakeModelPack.Fingerprint().Value,
            DateTimeOffset.Now,
            TimeSpan.FromMilliseconds(120),
            "fake-runtime",
            "cpu",
            1,
            [0.97f, 0.42f, 0.83f, 0.10f, 0.66f, 0.21f]),
            Generation = new GenerationInfo
            {
                Source = GenerationSource.Automatic1111,
                PositivePrompt = "masterpiece, 1girl, sunset, beach",
                NegativePrompt = "lowres, bad anatomy",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["steps"] = "28",
                    ["sampler"] = "DPM++ 2M",
                    ["cfg"] = "7.0",
                    ["seed"] = "123456789",
                },
                Resources =
            [
                new GenerationResource(GenerationResourceKind.Checkpoint, "animagine_v4", "a1b2c3", null),
            ],
            },
        };

    public static IReadOnlyList<ImageDocument> Session() =>
    [
        MakeImage("sunset_beach.png"),
        MakeImage("city_night.jpg", 1024, 768),
        MakeImage("portrait_01.webp", 832, 1216, AnalysisState.NotRun),
    ];
}
