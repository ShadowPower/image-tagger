using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Logging;
using ImageTagger.Infrastructure.Runtime;

namespace ImageTagger.App.Services;

/// <summary>
/// 识别执行服务：实现“识别当前 / 识别全部”端到端闭环（TASKS C-06/U-02，DESIGN 14.3/14.4）。
/// UI 无关，只操作 <see cref="ImageDocument"/>；状态机由调用方展示。
/// 单图固定 batch 1；批量只加入未识别 / 失败 / 取消 / 过期项，已成功且未过期则跳过。
/// </summary>
public sealed class RecognitionService
{
    private readonly IModelPackService _modelPacks;
    private readonly ITagInferenceService _inference;
    private readonly ISettingsStore _settings;
    private readonly ILogService? _log;
    private LoadedModelPack? _currentPack;

    /// <summary>创建识别执行服务；日志可选，其余依赖均不能为空。</summary>
    public RecognitionService(
        IModelPackService modelPacks,
        ITagInferenceService inference,
        ISettingsStore settings,
        ILogService? log = null)
    {
        _modelPacks = modelPacks ?? throw new ArgumentNullException(nameof(modelPacks));
        _inference = inference ?? throw new ArgumentNullException(nameof(inference));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _log = log;
    }

    /// <summary>当前已加载的模型包；未加载时为 null。</summary>
    public LoadedModelPack? CurrentPack => _currentPack;

    /// <summary>当前模型包 Id；未加载时为 null。</summary>
    public string? CurrentPackId => _currentPack?.Descriptor.Id;

    /// <summary>
    /// 确保用户配置的唯一外部模型包目录可用。
    /// </summary>
    public async Task<LoadedModelPack> EnsureModelAsync(string? preferredPackId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuredPath = _settings.Load().ModelPackPath;
        if (string.IsNullOrWhiteSpace(configuredPath))
            throw new TaggerException(TaggerErrorCode.ModelPackInvalid, "未配置模型，请在设置中配置模型包目录。");
        var loaded = await _modelPacks.LoadFromPathAsync(configuredPath, cancellationToken).ConfigureAwait(false);
        _currentPack = loaded;
        return loaded;
    }

    /// <summary>
    /// 单图识别（batch 1）：成功写回快照并标 Succeeded；取消标 Canceled 不写回；
    /// 其它异常标 Failed 并写中文 LastError（技术细节只记日志）。
    /// await 后检查取消与 Id 一致性，取消后不提交迟到结果。
    /// </summary>
    public async Task<PredictionSnapshot?> RecognizeOneAsync(
        ImageDocument image, LoadedModelPack pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(pack);

        // 文档即对象本身，Id 不可变；此处快照 Id 用于防串图一致性检查。
        string imageId = image.Id;

        if (cancellationToken.IsCancellationRequested)
        {
            image.AnalysisState = AnalysisState.Canceled;
            return null;
        }

        image.AnalysisState = AnalysisState.Running;
        image.LastError = null;

        PredictionSnapshot snapshot;
        try
        {
            snapshot = await _inference.InferAsync(image, pack, cancellationToken).ConfigureAwait(false);
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
        catch (TaggerException exception)
        {
            // TaggerException 的 Message 已是用户可读文案，直接展示并记日志。
            image.AnalysisState = AnalysisState.Failed;
            image.LastError = exception.Message;
            _log?.Error("单图识别失败。", exception);
            return null;
        }
        catch (Exception exception)
        {
            // 非预期异常不暴露技术细节，只记日志并给中文结论。
            image.AnalysisState = AnalysisState.Failed;
            image.LastError = "识别失败，请重试。";
            _log?.Error("单图识别失败。", exception);
            return null;
        }

        // 取消后不提交迟到结果；Id 检查防止文档被串写。
        if (cancellationToken.IsCancellationRequested ||
            !string.Equals(image.Id, imageId, StringComparison.Ordinal))
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
    /// 批量识别：已成功且指纹一致则跳过；其余逐项经 <see cref="RecognizeOneAsync"/>。
    /// 单图失败不终止；取消后剩余记 Canceled 且不提交迟到结果；每次完成都报告进度。
    /// </summary>
    public async Task<BatchInferenceResult> RecognizeAllAsync(
        IReadOnlyList<ImageDocument> images,
        LoadedModelPack pack,
        IProgress<(int completed, int total)>? progress,
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
        bool cancelSignaled = false;

        foreach (var image in images)
        {
            ArgumentNullException.ThrowIfNull(image);

            // 已成功且未过期（指纹一致）直接跳过，不触发推理。
            if (IsSkipped(image, pack))
            {
                skipped++;
                completed++;
                progress?.Report((completed, total));
                continue;
            }

            // 取消后剩余统一记 Canceled，不再提交推理。
            if (cancelSignaled || cancellationToken.IsCancellationRequested)
            {
                image.AnalysisState = AnalysisState.Canceled;
                canceled++;
                completed++;
                progress?.Report((completed, total));
                cancelSignaled = true;
                continue;
            }

            await RecognizeOneAsync(image, pack, cancellationToken).ConfigureAwait(false);

            if (image.AnalysisState == AnalysisState.Succeeded)
                succeeded++;
            else if (image.AnalysisState == AnalysisState.Canceled)
            {
                canceled++;
                cancelSignaled = true;
            }
            else if (image.AnalysisState == AnalysisState.Failed)
            {
                failed++;
                if (cancellationToken.IsCancellationRequested)
                    cancelSignaled = true;
            }
            else
            {
                // 不应到达的兜底：未知状态按失败计，避免计数丢失。
                failed++;
            }

            completed++;
            progress?.Report((completed, total));
        }

        return new BatchInferenceResult(succeeded, failed, skipped, canceled);
    }

    /// <summary>
    /// 模型切换时把指纹不一致且 Succeeded 的项标 Stale；旧结果保留，
    /// 是否进入 Prompt 由 PromptBuilder 按指纹判断，此处只标状态。
    /// 未识别项（Prediction 为 null 或非 Succeeded）一律不动。
    /// </summary>
    public void MarkStaleForModelChange(IEnumerable<ImageDocument> images, string newFingerprint)
    {
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(newFingerprint);

        foreach (var image in images)
        {
            if (image is null)
                continue;
            if (image.AnalysisState != AnalysisState.Succeeded)
                continue;
            if (image.Prediction is null)
                continue;
            if (string.Equals(image.Prediction.ModelFingerprint, newFingerprint, StringComparison.Ordinal))
                continue;
            image.AnalysisState = AnalysisState.Stale;
        }
    }

    /// <summary>关闭确认语义：运行中才需要弹窗确认，保持简单可测。</summary>
    public static bool ShouldConfirmExit(bool isRunning) => isRunning;

    // 已成功且指纹一致视为未过期，可跳过；其余一律需要（重新）识别。
    private static bool IsSkipped(ImageDocument image, LoadedModelPack pack) =>
        image.AnalysisState == AnalysisState.Succeeded
        && image.Prediction is not null
        && string.Equals(image.Prediction.ModelFingerprint, pack.Fingerprint.Value, StringComparison.Ordinal);
}
