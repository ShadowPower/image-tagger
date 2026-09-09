using System.Buffers;
using System.Diagnostics;
using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Single-session inference service with sigmoid activation (design 14.1/14.3, C-01/C-04).
/// Owns at most one <see cref="IInferenceSessionHandle"/> guarded by a
/// <see cref="SemaphoreSlim"/> gate; fingerprint changes replace the session.
/// <see cref="InferAsync"/> returns a new immutable snapshot — the caller
/// atomically writes it back to <see cref="ImageDocument.Prediction"/>.
/// </summary>
public sealed class TagInferenceService : ITagInferenceService, ITaggerModelAdapter, IDisposable
{
    private readonly IInferenceRuntimeFactory _factory;
    private readonly IPreprocessingExecutor _executor;
    private readonly AccelerationPreference _preference;
    private readonly ISettingsStore? _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IInferenceSessionHandle? _session;
    private string? _loadedFingerprint;
    private AccelerationPreference? _loadedPreference;
    private bool _disposed;

    public TagInferenceService(
        IInferenceRuntimeFactory factory,
        IPreprocessingExecutor executor,
        AccelerationPreference preference = AccelerationPreference.Auto,
        ISettingsStore? settings = null)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(executor);
        _factory = factory;
        _executor = executor;
        _preference = preference;
        _settings = settings;
    }

    public bool IsLoaded(ModelPackFingerprint fingerprint) =>
        _session is not null
        && _loadedFingerprint is not null
        && string.Equals(_loadedFingerprint, fingerprint.Value, StringComparison.Ordinal);

    public bool Supports(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return string.Equals(descriptor.Task, "multi-label-image-tagging", StringComparison.Ordinal)
            && (string.Equals(descriptor.Output.Activation, "sigmoid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(descriptor.Output.Activation, "none", StringComparison.OrdinalIgnoreCase));
    }

    public void Activate(ReadOnlySpan<float> logits, Span<float> probabilities)
    {
        // Adapter without descriptor context defaults to sigmoid; the
        // descriptor-aware overload below handles "none".
        SigmoidPostprocessor.Activate(logits, probabilities);
    }

    /// <summary>Descriptor-aware activation honoring "sigmoid" vs "none".</summary>
    public void Activate(ModelDescriptor descriptor, ReadOnlySpan<float> logits, Span<float> probabilities)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (string.Equals(descriptor.Output.Activation, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (logits.Length != probabilities.Length)
                throw new TaggerException(
                    TaggerErrorCode.ModelIncompatible,
                    $"logits 长度 {logits.Length} 与概率缓冲长度 {probabilities.Length} 不一致。");
            logits.CopyTo(probabilities);
            return;
        }

        SigmoidPostprocessor.Activate(logits, probabilities);
    }

    public async Task<PredictionSnapshot> InferAsync(
        ImageDocument image, LoadedModelPack pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(pack);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Supports(pack.Descriptor))
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"不支持的模型任务 {pack.Descriptor.Task} / 激活 {pack.Descriptor.Output.Activation}。");

        int labelCount = pack.Descriptor.Output.LabelCount;
        if (labelCount <= 0)
            throw new TaggerException(TaggerErrorCode.ModelIncompatible, "manifest labelCount 非法。");
        int elementCount = pack.Pipeline.PerSampleTensorContract.ElementCount;
        if (elementCount <= 0)
            throw new TaggerException(TaggerErrorCode.ModelIncompatible, "预处理 Tensor 契约非法。");

        var stopwatch = Stopwatch.StartNew();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        float[]? rentedInput = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preference = _preference;
            try
            {
                preference = _settings?.Load().Acceleration ?? preference;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A corrupt/unavailable settings file must not block CPU inference.
            }
            await EnsureSessionAsync(pack, preference, cancellationToken).ConfigureAwait(false);
            var session = _session ?? throw new TaggerException(
                TaggerErrorCode.InferenceFailed, "推理会话不可用。");

            rentedInput = ArrayPool<float>.Shared.Rent(elementCount);
            var inputMemory = new Memory<float>(rentedInput, 0, elementCount);
            await _executor.PreprocessIntoAsync(
                pack.Pipeline,
                new FileImageSource(image.CanonicalPath, image.LargeImageApproved),
                inputMemory,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var logits = new float[labelCount];
            // Native ORT calls cannot be aborted mid-flight; on cancellation the
            // caller discards the result (queue checks CT before write-back).
            session.Run(inputMemory.Span, 1, logits.AsSpan());
            cancellationToken.ThrowIfCancellationRequested();

            SigmoidPostprocessor.ValidateLength(logits.Length, labelCount);
            var probabilities = new float[labelCount];
            Activate(pack.Descriptor, logits, probabilities);
            cancellationToken.ThrowIfCancellationRequested();

            stopwatch.Stop();
            return new PredictionSnapshot(
                pack.Fingerprint.Value,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed,
                session.Runtime,
                session.ExecutionProvider,
                1,
                probabilities);
        }
        catch (OperationCanceledException exception)
        {
            throw new TaggerException(TaggerErrorCode.Canceled, "识别已取消。", exception);
        }
        finally
        {
            if (rentedInput is not null)
                ArrayPool<float>.Shared.Return(rentedInput);
            _gate.Release();
        }
    }

    private async Task EnsureSessionAsync(
        LoadedModelPack pack,
        AccelerationPreference preference,
        CancellationToken cancellationToken)
    {
        if (IsLoaded(pack.Fingerprint) && _loadedPreference == preference)
            return;

        _session?.Dispose();
        _session = null;
        _loadedFingerprint = null;
        _loadedPreference = null;

        var handle = await _factory.CreateAsync(
            pack.Descriptor, preference, cancellationToken).ConfigureAwait(false);

        try
        {
            // Reverse-check live session metadata against the manifest (14.1 step 6).
            if (handle is OnnxRuntimeSessionHandle ort)
                ort.ValidateDescriptor(pack.Descriptor.Input, pack.Descriptor.Input.Name, pack.Descriptor.Output.Name);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        _session = handle;
        _loadedFingerprint = pack.Fingerprint.Value;
        _loadedPreference = preference;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session?.Dispose();
        _session = null;
        _loadedPreference = null;
        _gate.Dispose();
    }
}
