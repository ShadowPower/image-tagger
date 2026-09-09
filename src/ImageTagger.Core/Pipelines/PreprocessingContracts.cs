using ImageTagger.Core.ModelPacks;

namespace ImageTagger.Core.Pipelines;

/// <summary>Data kinds flowing through preprocessing operators; the type chain must be linear and valid.</summary>
public enum TensorKind
{
    /// <summary>Encoded image bytes before the mandatory decode step.</summary>
    EncodedImage,

    /// <summary>Decoded pixel image with optional alpha.</summary>
    Image,

    /// <summary>8-bit RGB image without alpha.</summary>
    RgbImage,

    /// <summary>Float32 planar or interleaved numeric tensor.</summary>
    FloatTensor,
}

/// <summary>Element dtype of a model input tensor.</summary>
public enum TensorDType
{
    Float32,
}

/// <summary>Concrete tensor shape contract for one sample.</summary>
public sealed record ModelInputTensor
{
    public required TensorDType DType { get; init; }

    /// <summary>e.g. [3, 448, 448] per sample; batch is composed separately.</summary>
    public required int[] Shape { get; init; }

    /// <summary>"NCHW" or "NHWC"; first axis is ignored for per-sample contracts.</summary>
    public required string Layout { get; init; }

    public int ElementCount => Shape.Aggregate(1, (a, b) => a * b);
}

/// <summary>Source image abstraction handed to a compiled pipeline; implemented by Infrastructure.</summary>
public interface IImageSource
{
    /// <summary>Absolute canonical path of the source file.</summary>
    string Path { get; }

    bool AllowLargeImage { get; }
}

/// <summary>
/// Image source backed by a file on disk; the canonical path is the only
/// identity. Shared so the preprocessing and inference workflows agree on how a
/// pipeline is invoked without either one owning the other's types.
/// </summary>
public sealed record FileImageSource(string Path, bool AllowLargeImage = false) : IImageSource;

/// <summary>
/// Runs a compiled pipeline for exactly one image, writing the final sample
/// tensor straight into the caller-provided batch slice (design 3.4.1, 19).
/// Implementations return every pooled buffer on success, failure and
/// cancellation paths.
/// </summary>
public interface IPreprocessingExecutor
{
    ValueTask PreprocessIntoAsync(
        CompiledPreprocessingPipeline pipeline,
        IImageSource source,
        Memory<float> destination,
        CancellationToken cancellationToken);
}

/// <summary>A single compiled, versioned preprocessing operator.</summary>
public interface IPreprocessOperator
{
    /// <summary>Stable operator name retained in the immutable compiled plan.</summary>
    string Op { get; }
}

/// <summary>
/// Registry of versioned atomic operators with closed parameter schemas
/// (design 3.4.1). The factory is the only place that knows concrete operators.
/// </summary>
public interface IPreprocessOperatorFactory
{
    string Op { get; }

    IReadOnlySet<int> SupportedVersions { get; }

    /// <summary>Validates parameters against the closed schema; throws <c>TaggerException</c> on violation.</summary>
    void ValidateParameters(IReadOnlyDictionary<string, object?> parameters);

    /// <summary>Input/Output kind pair for the type-chain check.</summary>
    (TensorKind Input, TensorKind Output) GetKinds(IReadOnlyDictionary<string, object?> parameters);

    /// <summary>Computes the output shape from the input shape; throws when shapes are incompatible.</summary>
    (int Width, int Height, int Channels) InferShape(
        (int Width, int Height, int Channels) input,
        IReadOnlyDictionary<string, object?> parameters);

    IPreprocessOperator Compile(IReadOnlyDictionary<string, object?> parameters);
}

/// <summary>An executable, cached plan: validated steps + the exact tensor contract it produces.</summary>
public sealed class CompiledPreprocessingPipeline
{
    public required string Fingerprint { get; init; }

    public required ModelInputTensor PerSampleTensorContract { get; init; }

    /// <summary>Validated immutable step order retained for the execution plan.</summary>
    public required IReadOnlyList<PreprocessStepDescriptor> Steps { get; init; }

    public required IReadOnlyList<IPreprocessOperator> Operators { get; init; }
}

/// <summary>Compiles and validates an ordered step list against a model input contract (design 14.1 step 4).</summary>
public interface IPreprocessingPipelineCompiler
{
    /// <summary>Throws <c>TaggerException</c> with <see cref="Errors.TaggerErrorCode.ModelPackInvalid"/>
    /// when steps are unknown, mistyped, or produce a tensor mismatching the model input.</summary>
    CompiledPreprocessingPipeline ValidateAndCompile(
        PreprocessingPipelineDescriptor descriptor,
        ModelInputTensor modelInput);
}
