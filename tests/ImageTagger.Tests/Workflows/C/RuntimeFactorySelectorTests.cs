using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", "Unit")]
public sealed class RuntimeFactorySelectorTests
{
    [Fact]
    public void CpuOnly_and_PowerSaver_always_select_cpu()
    {
        var (cpu, windows, coreMl) = RuntimeFactorySelector.CreateDefault();
        Assert.Same(cpu, RuntimeFactorySelector.Select(AccelerationPreference.CpuOnly, cpu, windows, coreMl));
        Assert.Same(cpu, RuntimeFactorySelector.Select(AccelerationPreference.PowerSaver, cpu, windows, coreMl));
    }

    [Fact]
    public void Auto_selects_the_platform_hardware_factory_with_cpu_fallback()
    {
        var (cpu, windows, coreMl) = RuntimeFactorySelector.CreateDefault();
        IInferenceRuntimeFactory selected =
            RuntimeFactorySelector.Select(AccelerationPreference.Auto, cpu, windows, coreMl);

        if (OperatingSystem.IsWindows())
            Assert.Same(windows, selected);
        else if (OperatingSystem.IsMacOS())
            Assert.Same(coreMl, selected);
        else
            Assert.Same(cpu, selected);
    }

    [Fact]
    public void Fallback_handle_preserves_real_report_and_marks_downgrade()
    {
        var inner = new TagInferenceServiceTests.StubHandle(2);
        using var fallback = new FallbackInferenceSessionHandle(inner, fallbackOccurred: true);
        Assert.True(fallback.ProviderFallbackOccured);
        Assert.Equal(inner.Runtime, fallback.Runtime);
        Assert.Equal(inner.ExecutionProvider, fallback.ExecutionProvider);
        Assert.Equal(inner.Device, fallback.Device);

        float[] input = new float[8];
        float[] logits = new float[2];
        // Delegation: the wrapper forwards Run to the inner handle.
        var matching = new TagInferenceServiceTests.StubHandle(2);
        using var pass = new FallbackInferenceSessionHandle(matching, true);
        pass.Run(input, 1, logits);
        Assert.Equal(-2f, logits[0], precision: 5);
        Assert.Equal(-1f, logits[1], precision: 5);
    }
}
