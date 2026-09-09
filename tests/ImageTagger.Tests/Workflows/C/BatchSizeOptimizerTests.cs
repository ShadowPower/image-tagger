using ImageTagger.Core.Fakes;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", "Unit")]
public sealed class BatchSizeOptimizerTests
{
    [Fact]
    public void Static_batch1_model_never_enables_batching()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: true);
        var descriptor = FakeModelPack.Descriptor() with { Input = FakeModelPack.Descriptor().Input with { Batch = "static" } };

        Assert.Equal(1, optimizer.InitialBatchSize(descriptor));
        optimizer.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(1, optimizer.NextBatchSize(queueRemaining: 100));
    }

    [Fact]
    public void Numeric_batch1_contract_also_stays_single()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: false);
        var descriptor = FakeModelPack.Descriptor() with { Input = FakeModelPack.Descriptor().Input with { Batch = "1" } };
        Assert.Equal(1, optimizer.InitialBatchSize(descriptor));
        Assert.Equal(1, optimizer.NextBatchSize(100));
    }

    [Fact]
    public void Insufficient_gain_below_8_percent_does_not_upgrade()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: true);
        optimizer.InitialBatchSize(FakeModelPack.Descriptor());
        optimizer.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(2, optimizer.NextBatchSize(100));

        // +7.9% is below the bar: stays at 1 and seals the ceiling.
        optimizer.Observe(2, 10.79, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(1, optimizer.CurrentBest);
        Assert.Equal(1, optimizer.NextBatchSize(100));

        Assert.True(BatchSizeOptimizer.ShouldUpgrade(10, 10.8));
        Assert.False(BatchSizeOptimizer.ShouldUpgrade(10, 10.79));
    }

    [Fact]
    public void Oom_falls_back_and_retries_only_once()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: true);
        optimizer.InitialBatchSize(FakeModelPack.Descriptor());
        optimizer.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        optimizer.Observe(2, 12, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(2, optimizer.CurrentBest);

        optimizer.Observe(4, 0, memoryPressureHigh: false, outOfMemory: true);
        Assert.Equal(2, optimizer.CurrentBest);
        // The failed size is blocked: the next probe skips 4 even with a deep queue.
        Assert.NotEqual(4, optimizer.NextBatchSize(100));

        // A second OOM at the same size does not wedge the optimizer.
        optimizer.Observe(4, 0, memoryPressureHigh: false, outOfMemory: true);
        Assert.Equal(2, optimizer.CurrentBest);
    }

    [Fact]
    public void Memory_pressure_falls_back_like_oom()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: true);
        optimizer.InitialBatchSize(FakeModelPack.Descriptor());
        optimizer.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        optimizer.Observe(2, 0, memoryPressureHigh: true, outOfMemory: false);
        Assert.Equal(1, optimizer.CurrentBest);
    }

    [Fact]
    public void Tail_batch_uses_the_real_count()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: true);
        optimizer.InitialBatchSize(FakeModelPack.Descriptor());
        optimizer.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        optimizer.Observe(2, 12, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(1, optimizer.NextBatchSize(queueRemaining: 1));
        Assert.Equal(2, optimizer.NextBatchSize(queueRemaining: 4));
    }

    [Fact]
    public void Cpu_caps_at_4_and_hardware_caps_at_8()
    {
        var cpu = new BatchSizeOptimizer(isHardwareAccelerated: false);
        cpu.InitialBatchSize(FakeModelPack.Descriptor());
        cpu.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        cpu.Observe(2, 12, memoryPressureHigh: false, outOfMemory: false);
        cpu.Observe(4, 15, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(4, cpu.CurrentBest);
        Assert.Equal(4, cpu.NextBatchSize(100));

        var hardware = new BatchSizeOptimizer(isHardwareAccelerated: true);
        hardware.InitialBatchSize(FakeModelPack.Descriptor());
        hardware.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        hardware.Observe(2, 12, memoryPressureHigh: false, outOfMemory: false);
        hardware.Observe(4, 15, memoryPressureHigh: false, outOfMemory: false);
        Assert.Equal(4, hardware.CurrentBest);
        Assert.Equal(8, hardware.NextBatchSize(100));
    }

    [Fact]
    public void Short_queue_below_twice_candidate_does_not_upgrade()
    {
        var optimizer = new BatchSizeOptimizer(isHardwareAccelerated: true);
        optimizer.InitialBatchSize(FakeModelPack.Descriptor());
        optimizer.Observe(1, 10, memoryPressureHigh: false, outOfMemory: false);
        // 2×2=4 > 3 remaining: stay at 1.
        Assert.Equal(1, optimizer.NextBatchSize(queueRemaining: 3));
        Assert.Equal(2, optimizer.NextBatchSize(queueRemaining: 4));
    }
}
