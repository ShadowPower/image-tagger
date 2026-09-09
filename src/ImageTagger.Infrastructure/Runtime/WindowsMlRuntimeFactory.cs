using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Services;
using Microsoft.ML.OnnxRuntime;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Windows ML runtime factory, thin probe + CPU fallback (design 14.2, C-02).
/// Uses the Windows ML auto-initialized ORT EP devices directly; when a compatible EP
/// or hardware session creation is unavailable it falls back to ORT CPU and
/// returns a <see cref="FallbackInferenceSessionHandle"/> with
/// <c>ProviderFallbackOccured=true</c> carrying the truthful CPU EP report.
/// </summary>
public sealed class WindowsMlRuntimeFactory : IInferenceRuntimeFactory
{
    private readonly CpuInferenceRuntimeFactory _cpuFallback;
    private readonly Func<ModelDescriptor, string>? _resolver;

    public WindowsMlRuntimeFactory(Func<ModelDescriptor, string>? modelPathResolver = null)
    {
        _resolver = modelPathResolver;
        _cpuFallback = new CpuInferenceRuntimeFactory(modelPathResolver);
    }

    public async Task<IInferenceSessionHandle> CreateAsync(
        ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        // PowerSaver/CpuOnly never attempt hardware; go straight to CPU without
        // marking it as a "fallback" (it was the requested path).
        if (preference is AccelerationPreference.CpuOnly or AccelerationPreference.PowerSaver
            || !OperatingSystem.IsWindows())
        {
            return await _cpuFallback.CreateAsync(descriptor, preference, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var hardware = await TryCreateHardwareAsync(descriptor, cancellationToken).ConfigureAwait(false);
            return hardware;
        }
        catch (TaggerException exception) when (exception.Code == TaggerErrorCode.ProviderUnavailable)
        {
            var cpu = await _cpuFallback.CreateAsync(descriptor, preference, cancellationToken).ConfigureAwait(false);
            return new FallbackInferenceSessionHandle(cpu, fallbackOccurred: true);
        }
    }

    /// <summary>
    /// Probes Windows ML availability. The first thin version validates that the
    /// WindowsAppSDK.ML assembly loads and reports a usable device; actual
    /// hardware session creation lands here when the SDK is referenced by the
    /// Windows发行 project. Unavailable → <see cref="TaggerErrorCode.ProviderUnavailable"/>.
    /// </summary>
    public Task<IInferenceSessionHandle> TryCreateHardwareAsync(
        ModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();

        if (!OperatingSystem.IsWindows())
            throw new TaggerException(TaggerErrorCode.ProviderUnavailable, "Windows ML 仅在 Windows 上可用。");

        return Task.Run(() =>
        {
            try
            {
                var env = OrtEnv.Instance();
                var devices = env.GetEpDevices()
                    .Where(device => !string.Equals(device.EpName, "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (devices.Length == 0)
                    throw new TaggerException(TaggerErrorCode.ProviderUnavailable, "未发现 Windows ML 硬件执行设备。");

                var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                try
                {
                    options.AppendExecutionProvider(env, devices, new Dictionary<string, string>());
                    var hardware = devices[0].HardwareDevice;
                    var label = $"WindowsML-{hardware.Vendor}-{hardware.DeviceId:X}";
                    return (IInferenceSessionHandle)new OnnxRuntimeSessionHandle(
                        options, _resolver?.Invoke(descriptor) ?? descriptor.ModelFile, descriptor.Input.Name, descriptor.Output.Name,
                        executionProvider: devices[0].EpName, device: label,
                        channels: 3, height: descriptor.Input.Height, width: descriptor.Input.Width);
                }
                finally
                {
                    options.Dispose();
                }
            }
            catch (TaggerException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new TaggerException(TaggerErrorCode.ProviderUnavailable,
                    "Windows ML 硬件会话创建失败，已准备降级到 CPU。", exception);
            }
        }, cancellationToken);
    }

    /// <summary>True when the Windows ML SDK assembly loads in this process.</summary>
    public static bool IsWindowsMlAvailable()
    {
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            return OrtEnv.Instance().GetEpDevices().Any(device =>
                !string.Equals(device.EpName, "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
