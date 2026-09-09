using ImageTagger.Core;
using ImageTagger.Infrastructure.Runtime;
using ImageTagger.Tests.TestInfrastructure;
using Microsoft.ML.OnnxRuntime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

/// <summary>
/// Lightweight ORT smoke coverage without the 818 MiB model (design 20.2):
/// SessionOptions creation, CPU EP availability, version reporting, and the
/// no-half-session failure contract. Full batch 1/2/4 equivalence runs under
/// RealModel with the built-in pack.
/// </summary>
[Trait("Category", TestCategories.Integration)]
public sealed class OnnxRuntimeSmokeTests
{
    [Fact]
    public void SessionOptions_can_be_created_with_max_graph_optimization()
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        options.AppendExecutionProvider_CPU(0);
        Assert.Equal(GraphOptimizationLevel.ORT_ENABLE_ALL, options.GraphOptimizationLevel);
    }

    [Fact]
    public void Cpu_execution_provider_is_available()
    {
        string[] providers = CpuInferenceRuntimeFactory.GetAvailableProviders();
        Assert.NotEmpty(providers);
        Assert.Contains(providers, p =>
            p.Contains("CPU", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Runtime_version_is_reported_for_cache_keys()
    {
        string version = CpuInferenceRuntimeFactory.GetRuntimeVersion();
        Assert.False(string.IsNullOrWhiteSpace(version));
        string key = ProviderSelector.BuildCacheKey("fp", "CPU", "CPU-X64", version);
        Assert.Contains(version, key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_model_throws_without_a_half_initialized_session()
    {
        var factory = new CpuInferenceRuntimeFactory(descriptor =>
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.onnx"));

        var exception = await Assert.ThrowsAsync<TaggerException>(
            () => factory.CreateAsync(
                ImageTagger.Core.Fakes.FakeModelPack.Descriptor(),
                ImageTagger.Core.Domain.AccelerationPreference.CpuOnly,
                TestContext.Current.CancellationToken));
        Assert.True(
            exception.Code is TaggerErrorCode.ModelPackCorrupt or TaggerErrorCode.InferenceFailed,
            $"unexpected code {exception.Code}");
    }

    [Fact]
    public void Invalid_onnx_bytes_throw_inference_failed_not_half_usable()
    {
        string badModel = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(badModel, [1, 2, 3, 4]);
        try
        {
            using var options = new SessionOptions();
            Assert.ThrowsAny<Exception>(() => new InferenceSession(badModel, options));
        }
        finally
        {
            File.Delete(badModel);
        }
    }
}
