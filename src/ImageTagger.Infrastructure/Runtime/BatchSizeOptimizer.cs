using ImageTagger.Core.ModelPacks;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>
/// Online micro-batch probe 1→2→4→8 (design 14.4, C-07).
/// Pure decision logic — the caller owns cache keys and persistence.
/// CPU caps at 4, hardware EPs cap at 8; upgrades require ≥8% throughput gain
/// and enough queued work (2× candidate); OOM/memory pressure falls back and
/// never retries the same failed size within this instance.
/// </summary>
public sealed class BatchSizeOptimizer
{
    private static readonly int[] ProbeSequence = [1, 2, 4, 8];

    private readonly int _maxBatch;
    private readonly Dictionary<int, double> _throughputs = new();
    private readonly HashSet<int> _blocked = new();
    private int? _ceiling;
    private bool _isStatic;
    private int _currentBest = 1;
    private double _bestThroughput;

    public BatchSizeOptimizer(bool isHardwareAccelerated)
    {
        _maxBatch = isHardwareAccelerated ? 8 : 4;
    }

    /// <summary>Upgrade bar: candidate must beat current by at least 8%.</summary>
    public static bool ShouldUpgrade(double currentImagesPerSec, double candidateImagesPerSec)
    {
        if (currentImagesPerSec <= 0)
            return candidateImagesPerSec > 0;
        return candidateImagesPerSec >= currentImagesPerSec * 1.08;
    }

    /// <summary>Static batch-1 models never enable batching.</summary>
    public int InitialBatchSize(ModelDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(descriptor.Input.Batch, "dynamic", StringComparison.OrdinalIgnoreCase))
        {
            _isStatic = true;
            _currentBest = 1;
            return 1;
        }

        _isStatic = false;
        _currentBest = 1;
        _bestThroughput = 0;
        return 1;
    }

    /// <summary>Records one batch observation and adjusts the safe batch.</summary>
    public void Observe(int batchSize, double imagesPerSec, bool memoryPressureHigh, bool outOfMemory)
    {
        if (_isStatic)
            return;
        if (batchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(batchSize));

        if (outOfMemory || memoryPressureHigh)
        {
            // Fall back to the largest safe candidate below the failed size and
            // block the failed size so the affected batch is retried only once
            // (with the smaller batch) instead of probing the same size again.
            _blocked.Add(batchSize);
            int fallback = 1;
            foreach (int candidate in ProbeSequence)
            {
                if (candidate >= batchSize || candidate > _maxBatch)
                    break;
                if (_blocked.Contains(candidate))
                    continue;
                if (_ceiling.HasValue && candidate >= _ceiling.Value)
                    continue;
                fallback = candidate;
            }

            _currentBest = fallback;
            if (_throughputs.TryGetValue(fallback, out double fallbackThroughput))
                _bestThroughput = fallbackThroughput;
            return;
        }

        if (imagesPerSec < 0)
            throw new ArgumentOutOfRangeException(nameof(imagesPerSec));
        _throughputs[batchSize] = imagesPerSec;

        if (_bestThroughput <= 0)
        {
            _currentBest = batchSize;
            _bestThroughput = imagesPerSec;
            return;
        }

        if (batchSize > _currentBest)
        {
            // Trial of a larger batch: promote only on ≥8% gain, otherwise seal
            // the ceiling so larger probes are not attempted either.
            if (ShouldUpgrade(_bestThroughput, imagesPerSec))
            {
                _currentBest = batchSize;
                _bestThroughput = imagesPerSec;
            }
            else
            {
                _blocked.Add(batchSize);
                _ceiling = _ceiling.HasValue ? Math.Min(_ceiling.Value, batchSize) : batchSize;
            }
        }
        else if (batchSize == _currentBest)
        {
            _bestThroughput = imagesPerSec;
        }
    }

    /// <summary>
    /// Next batch size for the remaining queue. The tail uses its real count
    /// (no padding); upgrades require 2× candidate queued.
    /// </summary>
    public int NextBatchSize(int queueRemaining)
    {
        if (_isStatic)
            return 1;
        if (queueRemaining <= 0)
            return _currentBest;

        foreach (int candidate in ProbeSequence)
        {
            if (candidate <= _currentBest)
                continue;
            if (candidate > _maxBatch)
                break;
            if (_blocked.Contains(candidate))
                continue;
            if (_ceiling.HasValue && candidate >= _ceiling.Value)
                continue;
            if (queueRemaining < 2 * candidate)
                continue;
            return candidate;
        }

        // Tail batch: never pad with fake images.
        return Math.Min(_currentBest, queueRemaining);
    }

    /// <summary>Currently selected safe batch (test hook).</summary>
    public int CurrentBest => _currentBest;
}
