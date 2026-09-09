using ImageTagger.Core;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", "Unit")]
public sealed class ProviderSelectorTests
{
    [Fact]
    public void Cache_key_contains_model_provider_device_and_version()
    {
        string key = ProviderSelector.BuildCacheKey("fp1", "CoreML", "ANE", "1.29.0");
        Assert.Contains("fp1", key, StringComparison.Ordinal);
        Assert.Contains("CoreML", key, StringComparison.Ordinal);
        Assert.Contains("ANE", key, StringComparison.Ordinal);
        Assert.Contains("1.29.0", key, StringComparison.Ordinal);
    }

    [Fact]
    public void Environment_change_invalidates_the_cache()
    {
        Assert.True(ProviderSelector.IsCacheValid("env-a", "env-a"));
        Assert.False(ProviderSelector.IsCacheValid("env-a", "env-b"));
    }

    [Fact]
    public async Task Slower_hardware_loses_to_cpu_on_end_to_end_throughput()
    {
        var selector = new ProviderSelector();
        var candidates = new List<ProviderCandidate>
        {
            FakeCandidate("CoreML", "ANE", imagesPerSec: 5),
            FakeCandidate("CPU", "CPU-X64", imagesPerSec: 12),
        };

        var best = await selector.SelectBestAsync(
            candidates, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal("CPU", best.Provider);
        Assert.Equal(12, best.ImagesPerSec, precision: 5);
        Assert.False(best.TimedOut);
    }

    [Fact]
    public async Task Budget_timeout_returns_the_fastest_success_so_far()
    {
        var selector = new ProviderSelector();
        var candidates = new List<ProviderCandidate>
        {
            FakeCandidate("CPU", "CPU-X64", imagesPerSec: 9),
            HangingCandidate("CoreML", "ANE"),
            FakeCandidate("DML", "GPU", imagesPerSec: 99),
        };

        var best = await selector.SelectBestAsync(
            candidates, TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);

        Assert.Equal("CPU", best.Provider);
        Assert.True(best.TimedOut);
    }

    [Fact]
    public async Task Failing_provider_is_skipped_without_aborting_selection()
    {
        var selector = new ProviderSelector();
        var candidates = new List<ProviderCandidate>
        {
            new("Broken", "NPU",
                _ => Task.FromException(new InvalidOperationException("no driver")),
                _ => Task.FromResult(1000.0)),
            FakeCandidate("CPU", "CPU-X64", imagesPerSec: 7),
        };

        var best = await selector.SelectBestAsync(
            candidates, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal("CPU", best.Provider);
    }

    [Fact]
    public async Task No_successful_candidate_throws_provider_unavailable()
    {
        var selector = new ProviderSelector();
        var candidates = new List<ProviderCandidate>
        {
            new("Broken", "NPU",
                _ => Task.FromException(new InvalidOperationException("x")),
                _ => Task.FromResult(1.0)),
        };

        var exception = await Assert.ThrowsAsync<TaggerException>(
            () => selector.SelectBestAsync(
                candidates, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        Assert.Equal(TaggerErrorCode.ProviderUnavailable, exception.Code);
    }

    private static ProviderCandidate FakeCandidate(string provider, string device, double imagesPerSec) =>
        new(provider, device,
            _ => Task.CompletedTask,
            _ => Task.FromResult(imagesPerSec));

    private static ProviderCandidate HangingCandidate(string provider, string device) =>
        new(provider, device,
            ct => Task.Delay(TimeSpan.FromSeconds(30), ct),
            ct => Task.Delay(TimeSpan.FromSeconds(30), ct).ContinueWith(_ => 1.0, ct));
}
