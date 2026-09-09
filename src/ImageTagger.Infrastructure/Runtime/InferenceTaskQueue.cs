using System.Threading.Channels;
using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Runtime;

/// <summary>Summary of one batch run; failures never abort the remaining items.</summary>
public sealed record BatchInferenceResult(int Succeeded, int Failed, int Skipped, int Canceled);

/// <summary>
/// Bounded single/batch inference queue (design 14.4, C-06).
/// Single-image ("识别当前") runs immediately with batch 1 for lowest latency;
/// batch ("识别全部") flows through a bounded <see cref="Channel{T}"/> (capacity 8)
/// so memory stays bounded while keeping the single ORT session fed.
/// Per-image errors are isolated; cancellation discards late results and results
/// are always written back to the originating <see cref="ImageDocument"/> by Id.
/// </summary>
public sealed class InferenceTaskQueue
{
    private readonly Func<ImageDocument, LoadedModelPack, CancellationToken, Task<PredictionSnapshot>> _infer;
    private readonly int _capacity;

    public InferenceTaskQueue(
        Func<ImageDocument, LoadedModelPack, CancellationToken, Task<PredictionSnapshot>> inferFunc,
        int capacity = 8)
    {
        ArgumentNullException.ThrowIfNull(inferFunc);
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _infer = inferFunc;
        _capacity = capacity;
    }

    /// <summary>
    /// Runs one image with batch 1 and writes the snapshot back unless canceled.
    /// Returns null when cancellation won the race (no late commit).
    /// </summary>
    public async Task<PredictionSnapshot?> EnqueueSingleAsync(
        ImageDocument image, LoadedModelPack pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(pack);
        string imageId = image.Id;
        cancellationToken.ThrowIfCancellationRequested();

        image.AnalysisState = AnalysisState.Running;
        image.LastError = null;

        PredictionSnapshot snapshot;
        try
        {
            snapshot = await _infer(image, pack, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            image.AnalysisState = AnalysisState.Canceled;
            return null;
        }
        catch (TaggerException exception) when (exception.Code == TaggerErrorCode.Canceled)
        {
            image.AnalysisState = AnalysisState.Canceled;
            return null;
        }
        catch (Exception exception)
        {
            image.LastError = UserMessage(exception);
            image.AnalysisState = AnalysisState.Failed;
            throw;
        }

        // Cancel-after-complete must not commit a stale result; Id check guards
        // against the document being recycled for another file mid-flight.
        if (cancellationToken.IsCancellationRequested || !string.Equals(image.Id, imageId, StringComparison.Ordinal))
        {
            image.AnalysisState = AnalysisState.Canceled;
            return null;
        }

        image.Prediction = snapshot;
        image.AnalysisState = AnalysisState.Succeeded;
        image.LastError = null;
        return snapshot;
    }

    /// <summary>
    /// Runs a batch with skip/isolate/cancel semantics (design 14.4).
    /// Already-succeeded, unexpired images are skipped without invoking inference.
    /// </summary>
    public async Task<BatchInferenceResult> EnqueueBatchAsync(
        IReadOnlyList<ImageDocument> images,
        LoadedModelPack pack,
        IProgress<(int Completed, int Total)>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(pack);

        int total = images.Count;
        int completed = 0;
        int succeeded = 0;
        int failed = 0;
        int skipped = 0;
        int canceled = 0;

        // Pre-filter skips without touching the channel; everything else flows
        // through the bounded channel to bound in-flight work.
        var channel = Channel.CreateBounded<ImageDocument>(new BoundedChannelOptions(_capacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var toProcess = new List<ImageDocument>(images.Count);
        foreach (var image in images)
        {
            if (IsSkippable(image, pack.Fingerprint))
            {
                skipped++;
                completed++;
                progress?.Report((completed, total));
            }
            else
            {
                toProcess.Add(image);
            }
        }

        // Track the actual document instances. IDs are normally GUIDs, but the
        // queue must remain correct for callers that provide duplicate IDs.
        var seen = new HashSet<ImageDocument>(ReferenceEqualityComparer.Instance);
        bool cancelSignaled = false;

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var image in toProcess)
                    await channel.Writer.WriteAsync(image, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Reader drains what is already queued; unseen tail is swept below.
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var image in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            seen.Add(image);
            if (cancelSignaled || cancellationToken.IsCancellationRequested)
            {
                image.AnalysisState = AnalysisState.Canceled;
                canceled++;
                completed++;
                progress?.Report((completed, total));
                cancelSignaled = true;
                continue;
            }

            string imageId = image.Id;
            image.AnalysisState = AnalysisState.Running;
            image.LastError = null;

            PredictionSnapshot snapshot;
            try
            {
                snapshot = await _infer(image, pack, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                image.AnalysisState = AnalysisState.Canceled;
                canceled++;
                completed++;
                progress?.Report((completed, total));
                cancelSignaled = true;
                continue;
            }
            catch (TaggerException exception) when (exception.Code == TaggerErrorCode.Canceled)
            {
                image.AnalysisState = AnalysisState.Canceled;
                canceled++;
                completed++;
                progress?.Report((completed, total));
                cancelSignaled = true;
                continue;
            }
            catch (Exception exception)
            {
                image.LastError = UserMessage(exception);
                image.AnalysisState = AnalysisState.Failed;
                failed++;
                completed++;
                progress?.Report((completed, total));
                continue;
            }

            if (cancellationToken.IsCancellationRequested
                || !string.Equals(image.Id, imageId, StringComparison.Ordinal))
            {
                image.AnalysisState = AnalysisState.Canceled;
                canceled++;
            }
            else
            {
                image.Prediction = snapshot;
                image.AnalysisState = AnalysisState.Succeeded;
                image.LastError = null;
                succeeded++;
            }

            completed++;
            progress?.Report((completed, total));
        }

        // Sweep the never-enqueued tail (producer was canceled before WriteAsync).
        // Seen items were already counted; only unseen toProcess items remain.
        foreach (var image in toProcess)
        {
            if (seen.Contains(image))
                continue;
            image.AnalysisState = AnalysisState.Canceled;
            seen.Add(image);
            canceled++;
            completed++;
            progress?.Report((completed, total));
        }

        return new BatchInferenceResult(succeeded, failed, skipped, canceled);
    }

    internal static bool IsSkippable(ImageDocument image, ModelPackFingerprint fingerprint) =>
        image.AnalysisState == AnalysisState.Succeeded
        && image.Prediction is not null
        && string.Equals(image.Prediction.ModelFingerprint, fingerprint.Value, StringComparison.Ordinal);

    private static string UserMessage(Exception exception) => exception switch
    {
        TaggerException tagger => tagger.Message,
        _ => "识别失败，请重试。",
    };
}
