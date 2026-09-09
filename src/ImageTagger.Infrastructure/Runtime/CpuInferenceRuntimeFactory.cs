using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Services;
using Microsoft.ML.OnnxRuntime;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Default/test/fallback CPU runtime factory (design 14.2, C-01).
/// Always creates an ORT CPU <see cref="OnnxRuntimeSessionHandle"/> off the UI thread.
/// </summary>
public sealed class CpuInferenceRuntimeFactory : IInferenceRuntimeFactory
{
    private readonly Func<ModelDescriptor, string>? _modelPathResolver;

    /// <param name="modelPathResolver">
    /// Resolves the ONNX file for a descriptor. Defaults to
    /// <see cref="ModelDescriptor.ModelFile"/> as a literal path (tests pass an
    /// explicit resolver or a fixture path via the descriptor).
    /// Production wires a locator-based resolver (built-in + managed roots).
    /// </param>
    public CpuInferenceRuntimeFactory(Func<ModelDescriptor, string>? modelPathResolver = null)
    {
        _modelPathResolver = modelPathResolver;
    }

    public string ResolveModelPath(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (_modelPathResolver is not null)
            return _modelPathResolver(descriptor);
        return descriptor.ModelFile;
    }

    public async Task<IInferenceSessionHandle> CreateAsync(
        ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        string modelPath = ResolveModelPath(descriptor);
        if (!File.Exists(modelPath))
            throw new TaggerException(
                TaggerErrorCode.ModelPackCorrupt,
                $"找不到模型文件 {descriptor.ModelFile}，请重新安装应用。");

        // Never create the native session on the caller's (possibly UI) thread.
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            try
            {
                options.AppendExecutionProvider_CPU(0);
            }
            catch
            {
                // CPU is the default EP; a failed explicit append still yields CPU.
            }

            OnnxRuntimeSessionHandle? handle = null;
            try
            {
                handle = new OnnxRuntimeSessionHandle(
                    options,
                    modelPath,
                    descriptor.Input.Name,
                    descriptor.Output.Name,
                    executionProvider: "CPU",
                    channels: 3,
                    height: descriptor.Input.Height,
                    width: descriptor.Input.Width);
                var created = handle;
                handle = null;
                return (IInferenceSessionHandle)created;
            }
            catch
            {
                handle?.Dispose();
                throw;
            }
            finally
            {
                options.Dispose();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Truthfully enumerates ORT providers available in this process.</summary>
    public static string[] GetAvailableProviders()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders();
        }
        catch
        {
            return ["CPUExecutionProvider"];
        }
    }

    /// <summary>ORT build version for provider-cache keys.</summary>
    public static string GetRuntimeVersion()
    {
        try
        {
            return OrtEnv.Instance().GetVersionString();
        }
        catch
        {
            return "unknown";
        }
    }
}
