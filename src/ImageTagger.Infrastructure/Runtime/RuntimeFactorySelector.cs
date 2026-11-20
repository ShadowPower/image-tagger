using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Maps <see cref="AccelerationPreference"/> (settings) to the runtime factory
/// (design 11.3, C-01..C-03). Auto and HardwarePreferred use the platform
/// factory, which owns provider selection and CPU fallback.
/// </summary>
public static class RuntimeFactorySelector
{
    public static IInferenceRuntimeFactory Select(
        AccelerationPreference preference,
        IInferenceRuntimeFactory cpu,
        IInferenceRuntimeFactory windowsMl,
        IInferenceRuntimeFactory coreMl)
    {
        ArgumentNullException.ThrowIfNull(cpu);
        ArgumentNullException.ThrowIfNull(windowsMl);
        ArgumentNullException.ThrowIfNull(coreMl);

        return preference switch
        {
            AccelerationPreference.CpuOnly => cpu,
            AccelerationPreference.Auto or AccelerationPreference.HardwarePreferred => OperatingSystem.IsWindows() ? windowsMl
                : OperatingSystem.IsMacOS() ? coreMl
                : cpu,
            _ => cpu,
        };
    }

    /// <summary>Builds the default factory trio sharing one model-path resolver.</summary>
    public static (IInferenceRuntimeFactory Cpu, IInferenceRuntimeFactory Windows, IInferenceRuntimeFactory CoreMl)
        CreateDefault(Func<ImageTagger.Core.ModelPacks.ModelDescriptor, string>? modelPathResolver = null,
            bool benchmarkWindowsHardware = true) =>
        (
            new CpuInferenceRuntimeFactory(modelPathResolver),
            new WindowsMlRuntimeFactory(modelPathResolver, benchmarkWindowsHardware),
            new CoreMlRuntimeFactory(modelPathResolver)
        );
}
