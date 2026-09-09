namespace ImageTagger.Core;

/// <summary>Stable, user-facing error categories (design 18.1). UI maps these to messages.</summary>
public enum TaggerErrorCode
{
    None,
    ModelPackInvalid,
    ModelPackCorrupt,
    ModelIncompatible,
    ImageUnsupported,
    ImageCorrupt,
    InferenceFailed,
    OutOfMemory,
    ProviderUnavailable,
    SettingsCorrupt,
    IoError,
    Canceled,
}

/// <summary>
/// Single application error type carrying a machine-readable code plus a
/// user-safe message. Inner exceptions are logged, never shown raw in UI.
/// </summary>
public sealed class TaggerException : Exception
{
    public TaggerErrorCode Code { get; }

    public TaggerException(TaggerErrorCode code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
    }
}

/// <summary>Facts about the runtime that actually executed a prediction (shown in status bar and logs).</summary>
public sealed record ExecutionReport(
    string Runtime,
    string ExecutionProvider,
    string Device,
    int BatchSize,
    bool ProviderFallbackOccured);

/// <summary>Identity of an inference session/report from the runtime factory (design 14.2).</summary>
public interface IInferenceSessionHandle : IDisposable
{
    string Runtime { get; }

    string ExecutionProvider { get; }

    string Device { get; }

    /// <summary>Runs the model for one batch; input/output buffers are contiguous float32/logits.</summary>
    /// <remarks>Implementations must honor cancellation between batches; a native call
    /// that cannot be aborted completes and its result is discarded by the caller.</remarks>
    void Run(ReadOnlySpan<float> batchInput, int batchSamples, Span<float> logits);
}
