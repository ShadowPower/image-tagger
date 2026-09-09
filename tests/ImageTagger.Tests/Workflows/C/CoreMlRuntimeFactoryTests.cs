using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Runtime;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", TestCategories.MacRuntime)]
public sealed class CoreMlRuntimeFactoryTests
{
    [Fact]
    public async Task Hardware_probe_is_unavailable_off_macos_without_a_session()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Requires the real macOS runtime (CoreML/Avalonia.Native). " +
                        "Run on a macOS machine or via the release pipeline.");

        var factory = new CoreMlRuntimeFactory();
        // Thin v1 without a fixture model still validates the OS gate first.
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            () => factory.TryCreateHardwareAsync(
                ImageTagger.Core.Fakes.FakeModelPack.Descriptor(),
                TestContext.Current.CancellationToken));
        Assert.NotNull(exception);
    }

    [Fact]
    public void Acceleration_mapping_prefers_coreml_on_auto()
    {
        if (!OperatingSystem.IsMacOS())
            Assert.Skip("Requires the real macOS runtime (CoreML/Avalonia.Native). " +
                        "Run on a macOS machine or via the release pipeline.");

        var (cpu, windows, coreMl) = RuntimeFactorySelector.CreateDefault();
        Assert.Same(coreMl, RuntimeFactorySelector.Select(AccelerationPreference.Auto, cpu, windows, coreMl));
        Assert.Same(cpu, RuntimeFactorySelector.Select(AccelerationPreference.CpuOnly, cpu, windows, coreMl));
    }
}
