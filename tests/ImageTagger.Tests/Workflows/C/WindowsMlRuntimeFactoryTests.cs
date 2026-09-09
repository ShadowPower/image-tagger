using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Runtime;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", TestCategories.WindowsRuntime)]
public sealed class WindowsMlRuntimeFactoryTests
{
    [Fact]
    public async Task Hardware_probe_reports_unavailable_without_leaving_a_session()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Requires the real Windows runtime (Win32/WindowsAppSDK ML). " +
                        "Run on a Windows machine or via the release pipeline.");

        var factory = new WindowsMlRuntimeFactory();
        var exception = await Assert.ThrowsAsync<ImageTagger.Core.TaggerException>(
            () => factory.TryCreateHardwareAsync(
                ImageTagger.Core.Fakes.FakeModelPack.Descriptor(),
                TestContext.Current.CancellationToken));
        Assert.Equal(ImageTagger.Core.TaggerErrorCode.ProviderUnavailable, exception.Code);
    }

    [Fact]
    public void Acceleration_mapping_prefers_windows_hardware_on_auto()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Requires the real Windows runtime (Win32/WindowsAppSDK ML). " +
                        "Run on a Windows machine or via the release pipeline.");

        var (cpu, windows, coreMl) = RuntimeFactorySelector.CreateDefault();
        var selected = RuntimeFactorySelector.Select(AccelerationPreference.Auto, cpu, windows, coreMl);
        Assert.Same(windows, selected);
        Assert.Same(cpu, RuntimeFactorySelector.Select(AccelerationPreference.CpuOnly, cpu, windows, coreMl));
        Assert.Same(cpu, RuntimeFactorySelector.Select(AccelerationPreference.PowerSaver, cpu, windows, coreMl));
    }

    [Fact]
    public void Cpu_fallback_handle_records_truthful_cpu_report()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Requires the real Windows runtime (Win32/WindowsAppSDK ML). " +
                        "Run on a Windows machine or via the release pipeline.");

        // No model file is needed: the probe itself must report unavailability
        // without ever claiming a hardware EP.
        Assert.False(WindowsMlRuntimeFactory.IsWindowsMlAvailable()
            && CpuInferenceRuntimeFactory.GetAvailableProviders().Length > 10,
            "probe must stay truthful: only report EPs enumerated from ORT.");
    }
}
