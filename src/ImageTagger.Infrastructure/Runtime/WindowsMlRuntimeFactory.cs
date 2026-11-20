using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Services;

#if WINDOWS
using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.Windows.AI.MachineLearning;
using Serilog;
#endif

namespace ImageTagger.Infrastructure.Runtime;

public sealed class WindowsMlRuntimeFactory : IInferenceRuntimeFactory
{
    private readonly CpuInferenceRuntimeFactory _cpuFallback;

    public WindowsMlRuntimeFactory(Func<ModelDescriptor, string>? modelPathResolver = null) =>
        _cpuFallback = new CpuInferenceRuntimeFactory(modelPathResolver);

    public async Task<IInferenceSessionHandle> CreateAsync(
        ModelDescriptor descriptor, AccelerationPreference preference, CancellationToken cancellationToken)
    {
        if (preference != AccelerationPreference.Auto || !OperatingSystem.IsWindows())
            return await _cpuFallback.CreateAsync(descriptor, preference, cancellationToken).ConfigureAwait(false);

        var selected = await SelectAsync(descriptor, compareCpu: true, cancellationToken).ConfigureAwait(false);
        if (selected is not null)
            return selected;

        var cpu = await _cpuFallback.CreateAsync(descriptor, preference, cancellationToken).ConfigureAwait(false);
        return new FallbackInferenceSessionHandle(cpu, fallbackOccurred: true);
    }

    public async Task<IInferenceSessionHandle> TryCreateHardwareAsync(
        ModelDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsWindows())
        {
            var selected = await SelectAsync(descriptor, compareCpu: false, cancellationToken).ConfigureAwait(false);
            if (selected is not null)
                return selected;
        }
        throw new TaggerException(TaggerErrorCode.ProviderUnavailable, "未找到可运行此模型的 Windows ML 硬件执行设备。");
    }

#if WINDOWS
    private static readonly Lazy<Task> CatalogRegistration = new(() => Task.Run(RegisterCatalogAsync));
    private const string DirectMlProvider = "DmlExecutionProvider";

    private static async Task RegisterCatalogAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            return;

        try
        {
            var catalog = ExecutionProviderCatalog.GetDefault();
            try
            {
                await catalog.EnsureAndRegisterCertifiedAsync();
            }
            catch (Exception exception)
            {
                Log.Debug(exception, "Windows ML could not acquire certified providers; trying installed providers");
                await catalog.RegisterCertifiedAsync();
            }
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Windows ML catalog registration failed; trying built-in providers");
        }
    }

    private async Task<IInferenceSessionHandle?> SelectAsync(
        ModelDescriptor descriptor, bool compareCpu, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        string modelPath = _cpuFallback.ResolveModelPath(descriptor);
        if (!File.Exists(modelPath))
            throw new TaggerException(TaggerErrorCode.ModelPackCorrupt, $"找不到模型文件 {descriptor.ModelFile}。");

        await CatalogRegistration.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => SelectSession(descriptor, modelPath, compareCpu, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    private static IInferenceSessionHandle? SelectSession(
        ModelDescriptor descriptor, string modelPath, bool compareCpu, CancellationToken cancellationToken)
    {
        var input = new float[checked(3 * descriptor.Input.Height * descriptor.Input.Width)];
        var output = new float[descriptor.Output.LabelCount];
        var elapsed = Stopwatch.StartNew();
        var candidates = GetCandidates();
        OnnxRuntimeSessionHandle? best = null;
        TimeSpan bestTime = TimeSpan.MaxValue;
        int successfulVendorCandidates = 0;
        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= ProviderSelector.DefaultBudget)
                {
                    if (best is not null)
                        break;
                    if (!candidate.IsDirectMl)
                        continue;
                }
                if (!candidate.IsDirectMl && successfulVendorCandidates >= 2)
                    continue;
                if (Consider(candidate))
                {
                    if (candidate.IsDirectMl)
                        break;
                    successfulVendorCandidates++;
                }
            }

            if (best is not null && compareCpu && elapsed.Elapsed < ProviderSelector.DefaultBudget)
                Consider(new Candidate("CPU", null, null, IsDirectMl: false));

            cancellationToken.ThrowIfCancellationRequested();
            var selected = best;
            best = null;
            return selected;
        }
        finally
        {
            best?.Dispose();
        }

        bool Consider(Candidate candidate)
        {
            OnnxRuntimeSessionHandle? session = null;
            DirectoryInfo? profileDirectory = null;
            try
            {
                using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                if (candidate.Configure is not null)
                {
                    profileDirectory = Directory.CreateTempSubdirectory("image-tagger-ort-");
                    options.ProfileOutputPathPrefix = Path.Combine(profileDirectory.FullName, "assignment");
                    options.EnableProfiling = true;
                    candidate.Configure(options);
                }

                session = new OnnxRuntimeSessionHandle(
                    options, modelPath, descriptor.Input.Name, descriptor.Output.Name,
                    executionProvider: candidate.Provider, device: candidate.Device,
                    channels: 3, height: descriptor.Input.Height, width: descriptor.Input.Width,
                    runtime: candidate.Configure is null ? "ORT" : "Windows ML");
                session.ValidateDescriptor(descriptor.Input, descriptor.Input.Name, descriptor.Output.Name);
                cancellationToken.ThrowIfCancellationRequested();
                session.Run(input, 1, output);
                cancellationToken.ThrowIfCancellationRequested();
                if (profileDirectory is not null && !UsedProvider(session.EndProfiling(), candidate.Provider))
                {
                    Log.Debug("Windows ML provider {Provider} assigned no executed nodes", candidate.Provider);
                    return false;
                }

                long start = Stopwatch.GetTimestamp();
                session.Run(input, 1, output);
                TimeSpan duration = Stopwatch.GetElapsedTime(start);
                cancellationToken.ThrowIfCancellationRequested();
                if (output.Any(value => !float.IsFinite(value)))
                    return false;

                Log.Debug("Inference probe {Provider} ({Device}): {ElapsedMs:F1} ms",
                    candidate.Provider, candidate.Device, duration.TotalMilliseconds);
                if (duration < bestTime)
                {
                    best?.Dispose();
                    best = session;
                    session = null;
                    bestTime = duration;
                }
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                Log.Debug(exception, "Inference probe failed for {Provider} ({Device})", candidate.Provider, candidate.Device);
                return false;
            }
            finally
            {
                session?.Dispose();
                if (profileDirectory is not null)
                {
                    try
                    {
                        foreach (var file in profileDirectory.EnumerateFiles())
                            file.Delete();
                        profileDirectory.Delete();
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        Log.Debug(exception, "Could not clean up a Windows ML profiling trace");
                    }
                }
            }
        }
    }

    private static List<Candidate> GetCandidates()
    {
        var candidates = new List<Candidate>();
        try
        {
            var env = OrtEnv.Instance();
            var devices = env.GetEpDevices()
                .Where(device => device.HardwareDevice.Type is OrtHardwareDeviceType.NPU or OrtHardwareDeviceType.GPU)
                .GroupBy(device => (IsDirectMl: IsDirectMl(device.EpName), device.HardwareDevice.Type))
                .SelectMany(group => group.Select((device, index) => (Device: device, group.Key, Index: index)))
                .OrderBy(entry => entry.Key.IsDirectMl)
                .ThenBy(entry => entry.Index)
                .ThenBy(entry => entry.Key.Type == OrtHardwareDeviceType.NPU ? 0 : 1);
            foreach (var (device, key, _) in devices)
            {
                var hardware = device.HardwareDevice;
                var label = $"{hardware.Type}-{hardware.Vendor}-{hardware.DeviceId:X}";
                candidates.Add(new Candidate(device.EpName, label, options =>
                {
                    if (key.IsDirectMl)
                        ConfigureDirectMl(options);
                    options.AppendExecutionProvider(env, [device], new Dictionary<string, string>());
                }, key.IsDirectMl));
            }
        }
        catch (Exception exception)
        {
            Log.Debug(exception, "Windows ML could not enumerate ONNX Runtime hardware devices");
        }

        candidates.Add(new Candidate(DirectMlProvider, "GPU-DirectML-default", options =>
        {
            ConfigureDirectMl(options);
            options.AppendExecutionProvider_DML(0);
        }, IsDirectMl: true));
        return candidates;
    }

    private static bool IsDirectMl(string provider) =>
        string.Equals(provider, DirectMlProvider, StringComparison.OrdinalIgnoreCase);

    private static void ConfigureDirectMl(SessionOptions options)
    {
        options.EnableMemoryPattern = false;
        options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
    }

    private static bool UsedProvider(string profilePath, string provider)
    {
        using var stream = File.OpenRead(profilePath);
        using var profile = JsonDocument.Parse(stream);
        foreach (var trace in profile.RootElement.EnumerateArray())
        {
            if (trace.TryGetProperty("args", out var args) && args.TryGetProperty("provider", out var assigned)
                && assigned.ValueKind == JsonValueKind.String
                && string.Equals(assigned.GetString(), provider, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private sealed record Candidate(string Provider, string? Device, Action<SessionOptions>? Configure, bool IsDirectMl);
#else
    private static Task<IInferenceSessionHandle?> SelectAsync(
        ModelDescriptor descriptor, bool compareCpu, CancellationToken cancellationToken) =>
        Task.FromResult<IInferenceSessionHandle?>(null);
#endif
}
