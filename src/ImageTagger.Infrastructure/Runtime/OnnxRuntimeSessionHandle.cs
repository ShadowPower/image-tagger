using System.Runtime.InteropServices;
using ImageTagger.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// CPU-backed <see cref="IInferenceSessionHandle"/> over ORT <see cref="InferenceSession"/> (design 14.2, C-01).
/// Session creation is initiated off the UI thread by the owning factory via
/// <c>Task.Run</c>; this handle never touches UI dispatchers and never leaves a
/// half-initialized session behind (construction failure throws without returning).
/// </summary>
public sealed class OnnxRuntimeSessionHandle : IInferenceSessionHandle
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _outputName;
    private readonly int _channels;
    private readonly int _height;
    private readonly int _width;
    private readonly object _runLock = new();
    private bool _disposed;

    public string Runtime => "ORT";

    public string ExecutionProvider { get; }

    public string Device { get; }

    /// <summary>ONNX input name bound at construction.</summary>
    public string InputName => _inputName;

    /// <summary>ONNX output name bound at construction.</summary>
    public string OutputName => _outputName;

    /// <summary>Live ORT input metadata for descriptor reverse-checks.</summary>
    public IReadOnlyDictionary<string, NodeMetadata> InputMetadata => _session.InputMetadata;

    /// <summary>Live ORT output metadata for descriptor reverse-checks.</summary>
    public IReadOnlyDictionary<string, NodeMetadata> OutputMetadata => _session.OutputMetadata;

    /// <summary>
    /// Creates a CPU session. The caller must already be off the UI thread
    /// (factories use <c>Task.Run</c>); this constructor never returns a
    /// half-usable object — any failure disposes the native session and throws
    /// <see cref="TaggerException"/> with <see cref="TaggerErrorCode.InferenceFailed"/>.
    /// </summary>
    public OnnxRuntimeSessionHandle(
        SessionOptions options,
        string modelPath,
        string inputName,
        string outputName,
        string executionProvider = "CPU",
        string? device = null,
        int channels = 3,
        int height = 448,
        int width = 448)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputName);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputName);
        _inputName = inputName;
        _outputName = outputName;
        _channels = channels;
        _height = height;
        _width = width;
        ExecutionProvider = executionProvider;
        Device = device ?? $"CPU-{RuntimeInformation.ProcessArchitecture}";

        InferenceSession? session = null;
        try
        {
            session = new InferenceSession(modelPath, options);
            _session = session;
            session = null;
        }
        catch (TaggerException)
        {
            session?.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            session?.Dispose();
            throw new TaggerException(
                TaggerErrorCode.InferenceFailed,
                $"无法创建推理会话（{Path.GetFileName(modelPath)}）。",
                exception);
        }
    }

    /// <summary>
    /// Runs one batch. <paramref name="batchInput"/> is contiguous float32
    /// [N,C,H,W] and <paramref name="logits"/> receives contiguous [N,labelCount].
    /// </summary>
    public void Run(ReadOnlySpan<float> batchInput, int batchSamples, Span<float> logits)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (batchSamples <= 0)
            throw new TaggerException(TaggerErrorCode.InferenceFailed, $"batch 数量 {batchSamples} 非法。");
        int perSample = checked(_channels * _height * _width);
        if (batchInput.Length != checked(batchSamples * perSample))
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"输入长度 {batchInput.Length} 与 batch {batchSamples}×单样本 {perSample} 不匹配。");
        if (logits.Length == 0 || logits.Length % batchSamples != 0)
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"输出缓冲长度 {logits.Length} 与 batch {batchSamples} 不匹配。");

        // Copy spans to arrays: spans cannot be captured by Task.Run and the
        // ORT Tensor API takes Memory<T>. Batch=1 is ~2.4 MiB; batch=8 ~19 MiB.
        float[] inputArray = batchInput.ToArray();
        float[] outputArray = new float[logits.Length];

        lock (_runLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                var inputTensor = new DenseTensor<float>(
                    new Memory<float>(inputArray),
                    new ReadOnlySpan<int>([batchSamples, _channels, _height, _width]),
                    false);
                using var results = _session.Run(
                    new List<NamedOnnxValue>
                    {
                        NamedOnnxValue.CreateFromTensor(_inputName, inputTensor),
                    });

                DisposableNamedOnnxValue? output = null;
                foreach (var item in results)
                {
                    if (string.Equals(item.Name, _outputName, StringComparison.Ordinal))
                    {
                        output = item;
                        break;
                    }
                }

                output ??= results.Count > 0 ? results[0] : null;
                if (output is null)
                    throw new TaggerException(TaggerErrorCode.InferenceFailed, "模型没有返回任何输出。");

                var tensor = output.AsTensor<float>();
                if (tensor.Length != outputArray.Length)
                    throw new TaggerException(
                        TaggerErrorCode.ModelIncompatible,
                        $"模型输出长度 {tensor.Length} 与期望 {outputArray.Length} 不一致。");
                tensor.ToArray().CopyTo(outputArray, 0);
            }
            catch (TaggerException)
            {
                throw;
            }
            catch (OutOfMemoryException exception)
            {
                throw new TaggerException(TaggerErrorCode.OutOfMemory, "推理内存不足，已停止当前任务。", exception);
            }
            catch (Exception exception)
            {
                throw new TaggerException(TaggerErrorCode.InferenceFailed, "模型执行失败。", exception);
            }
        }

        outputArray.CopyTo(logits);
    }

    /// <summary>
    /// Reverse-checks session metadata against the manifest contract.
    /// Dtype must be float32; spatial dims must equal the descriptor input.
    /// </summary>
    public void ValidateDescriptor(ImageTagger.Core.ModelPacks.ModelInputContract input, string expectedInputName, string expectedOutputName)
    {
        if (!string.Equals(_inputName, expectedInputName, StringComparison.Ordinal))
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"会话输入名 {_inputName} 与 manifest {expectedInputName} 不一致。");
        if (!string.Equals(_outputName, expectedOutputName, StringComparison.Ordinal))
            throw new TaggerException(
                TaggerErrorCode.ModelIncompatible,
                $"会话输出名 {_outputName} 与 manifest {expectedOutputName} 不一致。");
        if (!_session.InputMetadata.TryGetValue(_inputName, out var meta) || meta is null)
            throw new TaggerException(TaggerErrorCode.ModelIncompatible, "会话缺少声明的输入元数据。");
        if (!meta.IsTensor || meta.ElementDataType != TensorElementType.Float)
            throw new TaggerException(TaggerErrorCode.ModelIncompatible, "会话输入不是 float32 Tensor。");
        // Dims are typically [N,C,H,W] with N symbolic (-1). Compare trailing dims when known.
        var dims = meta.Dimensions;
        if (dims is { Length: 4 })
        {
            // Allow dynamic batch (-1); require C/H/W to match when positive.
            if (dims[1] > 0 && dims[1] != _channels)
                throw new TaggerException(TaggerErrorCode.ModelIncompatible, "会话通道数与 manifest 不一致。");
            if (dims[2] > 0 && dims[2] != input.Height)
                throw new TaggerException(TaggerErrorCode.ModelIncompatible, "会话高度与 manifest 不一致。");
            if (dims[3] > 0 && dims[3] != input.Width)
                throw new TaggerException(TaggerErrorCode.ModelIncompatible, "会话宽度与 manifest 不一致。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Dispose();
    }
}
