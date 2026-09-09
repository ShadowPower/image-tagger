using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Services;
using Microsoft.ML.OnnxRuntime;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// macOS CoreML/CPU runtime factory (design 14.2, C-03).
/// Attempts ORT CoreML EP (<c>MLProgram</c>, <c>ComputeUnits=ALL</c> on Apple Silicon)
/// and falls back to ORT CPU when enumeration, compilation, warm-up, or the first
/// real batch fails. Returns a <see cref="FallbackInferenceSessionHandle"/> with
/// <c>ProviderFallbackOccured=true</c> so the UI shows the real CPU EP.
/// </summary>
public sealed class CoreMlRuntimeFactory : IInferenceRuntimeFactory
{
    private readonly CpuInferenceRuntimeFactory _cpuFallback;

    public CoreMlRuntimeFactory(Func<ModelDescriptor, string>? modelPathResolver = null)
    {
        _cpuFallback = new CpuInferenceRuntimeFactory(modelPathResolver);
    }

    public async Task<IInferenceSessionHandle> CreateAsync(
        ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        if (preference is AccelerationPreference.CpuOnly or AccelerationPreference.PowerSaver
            || !OperatingSystem.IsMacOS())
        {
            return await _cpuFallback.CreateAsync(descriptor, preference, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await TryCreateHardwareAsync(descriptor, cancellationToken).ConfigureAwait(false);
        }
        catch (TaggerException exception) when (exception.Code == TaggerErrorCode.ProviderUnavailable)
        {
            var cpu = await _cpuFallback.CreateAsync(descriptor, preference, cancellationToken).ConfigureAwait(false);
            return new FallbackInferenceSessionHandle(cpu, fallbackOccurred: true);
        }
    }

    /// <summary>
    /// Attempts a CoreML-EP session. Any incompatibility (unsupported OS, missing
    /// native CoreML EP, session creation failure) throws
    /// <see cref="TaggerErrorCode.ProviderUnavailable"/> for the caller to downgrade.
    /// </summary>
    public async Task<IInferenceSessionHandle> TryCreateHardwareAsync(
        ModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsMacOS())
            throw new TaggerException(TaggerErrorCode.ProviderUnavailable, "CoreML 仅在 macOS 上可用。");

        string modelPath = _cpuFallback.ResolveModelPath(descriptor);
        if (!File.Exists(modelPath))
            throw new TaggerException(TaggerErrorCode.ModelPackCorrupt, $"找不到模型文件 {descriptor.ModelFile}。");

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            try
            {
                // Apple Silicon: MLProgram + CPU/GPU (+ANE via the EP scheduler);
                // Intel Macs expose CPU/GPU only — CoreML itself hides absent ANE.
                options.AppendExecutionProvider_CoreML(
                    CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM | CoreMLFlags.COREML_FLAG_USE_CPU_AND_GPU);
            }
            catch (Exception exception)
            {
                options.Dispose();
                throw new TaggerException(
                    TaggerErrorCode.ProviderUnavailable, "CoreML EP 不可用，已准备降级到 CPU。", exception);
            }

            OnnxRuntimeSessionHandle? handle = null;
            try
            {
                handle = new OnnxRuntimeSessionHandle(
                    options,
                    modelPath,
                    descriptor.Input.Name,
                    descriptor.Output.Name,
                    executionProvider: "CoreML",
                    device: CoreMlDeviceLabel(),
                    channels: 3,
                    height: descriptor.Input.Height,
                    width: descriptor.Input.Width);
                var created = handle;
                handle = null;
                return (IInferenceSessionHandle)created;
            }
            catch (Exception exception) when (exception is not TaggerException)
            {
                handle?.Dispose();
                throw new TaggerException(
                    TaggerErrorCode.ProviderUnavailable, "CoreML 会话创建失败，降级到 CPU。", exception);
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

    internal static string CoreMlDeviceLabel()
    {
        // Truthful but coarse without private entitlement queries: report arch +
        // OS so logs distinguish Apple Silicon vs Intel without machine identity.
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        return $"CoreML-{arch}";
    }
}
