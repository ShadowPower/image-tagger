using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Preprocessing;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.C;

[Trait("Category", "Unit")]
public sealed class InferenceTaskQueueTests
{
    [Fact]
    public async Task Single_image_runs_batch1_and_writes_back_by_id()
    {
        var pack = QueuePack();
        var image = QueueImage();
        string capturedId = image.Id;
        var queue = new InferenceTaskQueue(async (doc, p, ct) =>
        {
            Assert.Equal(capturedId, doc.Id);
            await Task.Delay(1, ct);
            return Snapshot(p, [0.9f, 0.1f]);
        });

        var snapshot = await queue.EnqueueSingleAsync(image, pack, TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Same(snapshot, image.Prediction);
        Assert.Equal(AnalysisState.Succeeded, image.AnalysisState);
        Assert.Null(image.LastError);
    }

    [Fact]
    public async Task Batch_skips_already_succeeded_unexpired_images()
    {
        var pack = QueuePack(fingerprint: "fp-skip");
        var fresh = QueueImage();
        var done = QueueImage();
        done.AnalysisState = AnalysisState.Succeeded;
        done.Prediction = Snapshot(pack, [0.8f, 0.2f]);
        var stale = QueueImage();
        stale.AnalysisState = AnalysisState.Succeeded;
        stale.Prediction = Snapshot(pack, [0.1f, 0.9f]) with { ModelFingerprint = "other-fp" };
        int calls = 0;
        var queue = new InferenceTaskQueue((doc, p, ct) =>
        {
            calls++;
            return Task.FromResult(Snapshot(p, [0.5f, 0.5f]));
        });

        var result = await queue.EnqueueBatchAsync(
            [fresh, done, stale], pack, null, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Equal(new BatchInferenceResult(2, 0, 1, 0), result);
        Assert.Equal(AnalysisState.Succeeded, done.AnalysisState);
        Assert.Equal(0.8f, done.Prediction!.Probabilities[0], precision: 5);
    }

    [Fact]
    public async Task Single_failure_does_not_abort_the_batch()
    {
        var pack = QueuePack();
        var first = QueueImage();
        var bad = QueueImage();
        var last = QueueImage();
        var queue = new InferenceTaskQueue((doc, p, ct) =>
        {
            if (ReferenceEquals(doc, bad))
                throw new TaggerException(TaggerErrorCode.InferenceFailed, "单图坏了");
            return Task.FromResult(Snapshot(p, [0.6f, 0.4f]));
        });
        var progress = new CollectingProgress();

        var result = await queue.EnqueueBatchAsync(
            [first, bad, last], pack, progress, TestContext.Current.CancellationToken);

        Assert.Equal(new BatchInferenceResult(2, 1, 0, 0), result);
        Assert.Equal(AnalysisState.Succeeded, first.AnalysisState);
        Assert.Equal(AnalysisState.Failed, bad.AnalysisState);
        Assert.Equal("单图坏了", bad.LastError);
        Assert.Equal(AnalysisState.Succeeded, last.AnalysisState);
        Assert.Equal((3, 3), progress.Reports[^1]);
    }

    [Fact]
    public async Task Cancellation_does_not_commit_late_results()
    {
        var pack = QueuePack();
        var first = QueueImage();
        var second = QueueImage();
        using var cancellation = new CancellationTokenSource();
        var queue = new InferenceTaskQueue(async (doc, p, ct) =>
        {
            if (ReferenceEquals(doc, first))
            {
                await Task.Delay(50, ct);
                return Snapshot(p, [0.9f, 0.1f]);
            }

            // Second image: cancel while "inferring", then return late.
            await Task.Delay(200, CancellationToken.None);
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Snapshot(p, [0.1f, 0.9f]);
        });

        // Cancel during the first inference so its late completion must be dropped.
        cancellation.CancelAfter(10);
        var result = await queue.EnqueueBatchAsync(
            [first, second], pack, null, cancellation.Token);

        Assert.Equal(AnalysisState.Canceled, first.AnalysisState);
        Assert.Null(first.Prediction);
        Assert.True(result.Canceled >= 1);
        Assert.Equal(0, result.Succeeded);
    }

    [Fact]
    public async Task Results_are_written_to_the_originating_id_without_cross_talk()
    {
        var pack = QueuePack();
        var images = Enumerable.Range(0, 5).Select(_ => QueueImage()).ToArray();
        // Slow early images, fast late images: completion order differs from input order.
        var queue = new InferenceTaskQueue(async (doc, p, ct) =>
        {
            int index = Array.IndexOf(images, doc);
            await Task.Delay((5 - index) * 10, ct);
            return Snapshot(p, [(index + 1) / 10f, 1f - ((index + 1) / 10f)]);
        });

        await queue.EnqueueBatchAsync(images, pack, null, TestContext.Current.CancellationToken);

        for (int i = 0; i < images.Length; i++)
        {
            Assert.Equal(AnalysisState.Succeeded, images[i].AnalysisState);
            Assert.Equal((i + 1) / 10f, images[i].Prediction!.Probabilities[0], precision: 5);
        }
    }

    internal static LoadedModelPack QueuePack(string fingerprint = "fp-queue")
    {
        var descriptor = FakeModelPack.Descriptor(labelCount: 2, width: 8, height: 8);
        var catalog = new TagCatalog([
            new TagCatalogEntry(0, "sunny", "晴天", "subject"),
            new TagCatalogEntry(1, "night", "夜晚", "subject"),
        ]);
        var pipeline = new PreprocessingPipelineCompiler().ValidateAndCompile(
            descriptor.Preprocessing,
            new ModelInputTensor { DType = TensorDType.Float32, Layout = "NCHW", Shape = [3, 8, 8] });
        return new LoadedModelPack(
            descriptor, catalog, FakeModelPack.Fingerprint(fingerprint) with { Value = fingerprint }, pipeline);
    }

    internal static ImageDocument QueueImage() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CanonicalPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png"),
        FileName = "q.png",
        FileSize = 10,
        Format = "png",
        PixelWidth = 8,
        PixelHeight = 8,
    };

    private static PredictionSnapshot Snapshot(LoadedModelPack pack, float[] probs) =>
        new(pack.Fingerprint.Value, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1), "test", "CPU", 1, probs);

    private sealed class CollectingProgress : IProgress<(int Completed, int Total)>
    {
        public List<(int Completed, int Total)> Reports { get; } = [];
        public void Report((int Completed, int Total) value) => Reports.Add(value);
    }
}
