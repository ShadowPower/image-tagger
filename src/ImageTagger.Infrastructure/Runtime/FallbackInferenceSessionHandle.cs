using ImageTagger.Core;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Marks a handle produced via CPU fallback so the UI can show the real EP plus
/// a single downgrade notice (design 14.2). Delegates all execution to the inner handle.
/// </summary>
public sealed class FallbackInferenceSessionHandle : IInferenceSessionHandle
{
    private readonly IInferenceSessionHandle _inner;

    public FallbackInferenceSessionHandle(IInferenceSessionHandle inner, bool fallbackOccurred)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        ProviderFallbackOccured = fallbackOccurred;
    }

    public bool ProviderFallbackOccured { get; }

    public string Runtime => _inner.Runtime;

    public string ExecutionProvider => _inner.ExecutionProvider;

    public string Device => _inner.Device;

    public void Run(ReadOnlySpan<float> batchInput, int batchSamples, Span<float> logits) =>
        _inner.Run(batchInput, batchSamples, logits);

    public void Dispose() => _inner.Dispose();
}
