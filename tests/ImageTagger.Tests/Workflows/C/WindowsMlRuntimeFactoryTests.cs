using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Infrastructure.Runtime;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", TestCategories.WindowsRuntime)]
public sealed class WindowsMlRuntimeFactoryTests
{
    [Fact]
    public async Task Hardware_probe_rejects_missing_model_before_provider_discovery()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Requires the real Windows runtime (Win32/WindowsAppSDK ML). " +
                        "Run on a Windows machine or via the release pipeline.");

        using var temporary = new TempDirectory();
        var factory = new WindowsMlRuntimeFactory(_ => Path.Combine(temporary.FullPath, "missing.onnx"));
        var exception = await Assert.ThrowsAsync<TaggerException>(
            () => factory.TryCreateHardwareAsync(
                FakeModelPack.Descriptor(),
                TestContext.Current.CancellationToken));
#if WINDOWS
        Assert.Equal(TaggerErrorCode.ModelPackCorrupt, exception.Code);
#else
        Assert.Equal(TaggerErrorCode.ProviderUnavailable, exception.Code);
#endif
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
        Assert.Same(windows, RuntimeFactorySelector.Select(AccelerationPreference.HardwarePreferred, cpu, windows, coreMl));
    }

    [Fact]
    public async Task Cpu_only_runs_onnx_without_hardware_discovery()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Requires Windows.");

        using var temporary = new TempDirectory();
        var factory = new WindowsMlRuntimeFactory(_ => WriteAddModel(temporary));
        using var session = await factory.CreateAsync(
            AddModelDescriptor(), AccelerationPreference.CpuOnly, TestContext.Current.CancellationToken);
        Assert.Equal("CPU", session.ExecutionProvider);
        AssertDoublesInput(session);
    }

#if WINDOWS
    [Fact]
    public async Task Hardware_probe_runs_onnx_on_a_profile_verified_provider()
    {
        if (Environment.GetEnvironmentVariable("IMAGETAGGER_TEST_WINDOWS_ACCELERATION") != "1")
            Assert.Skip("Requires compatible Windows GPU or NPU. Set IMAGETAGGER_TEST_WINDOWS_ACCELERATION=1 to test for actual acceleration.");

        using var temporary = new TempDirectory();
        string modelPath = WriteAddModel(temporary);
        var factory = new WindowsMlRuntimeFactory(_ => modelPath);
        using var session = await factory.TryCreateHardwareAsync(AddModelDescriptor(), TestContext.Current.CancellationToken);
        Assert.Equal("Windows ML", session.Runtime);
        Assert.NotEqual("CPU", session.ExecutionProvider);
        AssertDoublesInput(session);
        TestContext.Current.SendDiagnosticMessage($"{session.ExecutionProvider} on {session.Device}");
    }

    [Theory]
    [InlineData(AccelerationPreference.Auto)]
    [InlineData(AccelerationPreference.HardwarePreferred)]
    public async Task Acceleration_mode_executes_model_with_the_selected_provider(AccelerationPreference preference)
    {
        if (Environment.GetEnvironmentVariable("IMAGETAGGER_TEST_WINDOWS_ACCELERATION") != "1")
            Assert.Skip("Set IMAGETAGGER_TEST_WINDOWS_ACCELERATION=1 to test automatic selection.");

        using var temporary = new TempDirectory();
        string modelPath = WriteAddModel(temporary);
        var factory = new WindowsMlRuntimeFactory(_ => modelPath);
        using var session = await factory.CreateAsync(
            AddModelDescriptor(), preference, TestContext.Current.CancellationToken);
        if (preference == AccelerationPreference.HardwarePreferred)
        {
            Assert.Equal("Windows ML", session.Runtime);
            Assert.NotEqual("CPU", session.ExecutionProvider);
        }
        AssertDoublesInput(session);
        TestContext.Current.SendDiagnosticMessage($"{preference} selected {session.ExecutionProvider} on {session.Device}");
    }

    [Theory]
    [InlineData(AccelerationPreference.Auto, true)]
    [InlineData(AccelerationPreference.HardwarePreferred, true)]
    [InlineData(AccelerationPreference.HardwarePreferred, false)]
    public async Task Only_benchmarked_hardware_selection_requires_profiled_nodes(AccelerationPreference preference, bool benchmarkHardware)
    {
        if (Environment.GetEnvironmentVariable("IMAGETAGGER_TEST_WINDOWS_ACCELERATION") != "1")
            Assert.Skip("Set IMAGETAGGER_TEST_WINDOWS_ACCELERATION=1 to test automatic selection.");

        using var temporary = new TempDirectory();
        string modelPath = temporary.WriteFile("passthrough.onnx", Convert.FromBase64String(
            "CAg6UBIMcGFzc190aHJvdWdoWh8KBWlucHV0EhYKFAgBEhAKAggBCgIIAwoCCAIKAggCYh8KBWlucHV0EhYKFAgBEhAKAggBCgIIAwoCCAIKAggCQgIQDQ=="));
        var descriptor = AddModelDescriptor();
        descriptor = descriptor with { Output = descriptor.Output with { Name = descriptor.Input.Name } };
        var factory = new WindowsMlRuntimeFactory(_ => modelPath, benchmarkHardware);

        using var session = await factory.CreateAsync(descriptor, preference, TestContext.Current.CancellationToken);
        if (benchmarkHardware)
        {
            Assert.True(Assert.IsType<FallbackInferenceSessionHandle>(session).ProviderFallbackOccured);
            Assert.Equal("CPU", session.ExecutionProvider);
        }
        else
        {
            Assert.Equal("Windows ML", session.Runtime);
            Assert.NotEqual("CPU", session.ExecutionProvider);
        }
        float[] input = Enumerable.Range(0, 12).Select(value => (float)value).ToArray();
        var output = new float[input.Length];
        session.Run(input, 1, output);
        Assert.Equal(input, output);
    }
#endif

    private static string WriteAddModel(TempDirectory temporary) => temporary.WriteFile(
        "add.onnx", Convert.FromBase64String(
            "CAg6egoYCgVpbnB1dAoFaW5wdXQSA3N1bSIDQWRkChYKA3N1bRIGb3V0cHV0IgdGbGF0dGVuEgt3aW5tbF9zbW9rZVofCgVpbnB1dBIWChQIARIQCgIIAQoCCAMKAggCCgIIAmIYCgZvdXRwdXQSDgoMCAESCAoCCAEKAggMQgIQDQ=="));

    private static ImageTagger.Core.ModelPacks.ModelDescriptor AddModelDescriptor()
    {
        var descriptor = FakeModelPack.Descriptor();
        return descriptor with
        {
            Input = descriptor.Input with { Name = "input", Height = 2, Width = 2 },
            Output = descriptor.Output with { Name = "output", LabelCount = 12 },
        };
    }

    private static void AssertDoublesInput(IInferenceSessionHandle session)
    {
        float[] input = Enumerable.Range(0, 12).Select(value => (float)value).ToArray();
        var output = new float[input.Length];
        session.Run(input, 1, output);
        Assert.Equal(input.Select(value => value * 2), output);
    }
}
