namespace ImageTagger.Infrastructure.Runtime;

/// <summary>A benchmarkable execution-provider candidate (design 14.2, C-05).</summary>
public sealed record ProviderCandidate(
    string Provider,
    string Device,
    Func<CancellationToken, Task> Warmup,
    Func<CancellationToken, Task<double>> Benchmark);

/// <summary>Winning provider plus whether the budget expired first.</summary>
public sealed record ProviderSelection(string Provider, string Device, double ImagesPerSec, bool TimedOut);

/// <summary>
/// Budgeted provider auto-selection: warms up and short-benchmarks at most two
/// compatible hardware candidates (plus the CPU baseline supplied by the caller)
/// and picks the fastest end-to-end images/sec. Total foreground budget is 20 s;
/// on timeout the fastest successful candidate so far wins (design 14.2, C-05).
/// Cache keys isolate model+provider+device+runtime; callers own persistence.
/// </summary>
public sealed class ProviderSelector
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20);

    /// <summary>Cache key isolating tuning results per model/provider/device/runtime.</summary>
    public static string BuildCacheKey(
        string modelFingerprint, string provider, string device, string runtimeVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeVersion);
        return string.Join('|', modelFingerprint, provider, device, runtimeVersion);
    }

    /// <summary>True when the cached environment fingerprint still matches.</summary>
    public static bool IsCacheValid(string cachedEnvironment, string currentEnvironment) =>
        string.Equals(cachedEnvironment, currentEnvironment, StringComparison.Ordinal);

    /// <summary>
    /// Benchmarks candidates in order within <paramref name="budget"/>.
    /// Failures are skipped; timeout returns the fastest success so far.
    /// </summary>
    /// <exception cref="ImageTagger.Core.TaggerException">
    /// Thrown with <see cref="ImageTagger.Core.TaggerErrorCode.ProviderUnavailable"/>
    /// when no candidate succeeds.
    /// </exception>
    public async Task<ProviderSelection> SelectBestAsync(
        IReadOnlyList<ProviderCandidate> candidates,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
            throw new ArgumentException("at least one candidate is required", nameof(candidates));
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        ProviderSelection? best = null;
        bool timedOut = false;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = stopwatch.Elapsed;
            if (elapsed >= budget)
            {
                timedOut = true;
                break;
            }

            var remaining = budget - elapsed;
            using var budgetScope = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budgetScope.CancelAfter(remaining);

            try
            {
                await candidate.Warmup(budgetScope.Token).ConfigureAwait(false);
                double imagesPerSec = await candidate.Benchmark(budgetScope.Token).ConfigureAwait(false);
                if (double.IsNaN(imagesPerSec) || double.IsInfinity(imagesPerSec) || imagesPerSec < 0)
                    continue;
                if (best is null || imagesPerSec > best.ImagesPerSec)
                    best = new ProviderSelection(candidate.Provider, candidate.Device, imagesPerSec, false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Budget slice expired: stop probing and keep the fastest so far.
                timedOut = true;
                break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One bad provider never aborts selection; try the next candidate.
                continue;
            }
        }

        stopwatch.Stop();
        if (best is null)
            throw new ImageTagger.Core.TaggerException(
                ImageTagger.Core.TaggerErrorCode.ProviderUnavailable,
                "没有可用的推理 provider。");

        // A creatable-but-slower hardware provider loses to the CPU baseline the
        // caller included in <paramref name="candidates"/>: fastest wins, so no
        // special-casing is needed beyond end-to-end comparison.
        return best with { TimedOut = timedOut || stopwatch.Elapsed >= budget };
    }
}
