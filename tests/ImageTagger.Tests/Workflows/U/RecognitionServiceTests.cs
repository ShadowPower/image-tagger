using ImageTagger.App.Services;
using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Fakes;
using ImageTagger.Core.ModelPacks;
using ImageTagger.Core.Pipelines;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Logging;
using ImageTagger.Infrastructure.Runtime;
using Xunit;

namespace ImageTagger.Tests.Workflows.U;

/// <summary>
/// 识别执行服务单测（TASKS C-06/U-02，DESIGN 14.3/14.4）。
/// 用内存假服务验证选包、单图写回、批量跳过 / 隔离 / 取消 / 进度 / 按 Id 回填与过期标记。
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecognitionServiceTests
{
    // ---------- EnsureModelAsync：选包 ----------

    [Trait("Category", "Unit")]
    [Fact]
    public async Task EnsureModel_without_configured_path_is_rejected()
    {
        var packs = new FakeModelPackService([
            ("custom-pack", false, true, "fp-custom"),
            (FakeModelPack.Id, true, true, "fp-builtin"),
        ]);
        var service = NewService(packs, new FakeTagInferenceService(), "");

        await Assert.ThrowsAsync<TaggerException>(() => service.EnsureModelAsync(null, TestContext.Current.CancellationToken));
    }

    [Trait("Category", "Unit")]
    [Fact(Skip = "Model selection by ID was removed; the application now uses one external directory.")]
    public async Task EnsureModel_preferred_hit_wins_over_builtin()
    {
        var packs = new FakeModelPackService([
            ("custom-pack", false, true, "fp-custom"),
            (FakeModelPack.Id, true, true, "fp-builtin"),
        ]);
        var service = NewService(packs, new FakeTagInferenceService(), "");

        var loaded = await service.EnsureModelAsync("custom-pack", TestContext.Current.CancellationToken);

        Assert.Equal("custom-pack", loaded.Descriptor.Id);
    }

    [Trait("Category", "Unit")]
    [Fact(Skip = "Model selection by ID was removed; the application now uses one external directory.")]
    public async Task EnsureModel_invalid_preferred_falls_back_to_available_external_pack()
    {
        var packs = new FakeModelPackService([
            ("custom-pack", false, true, "fp-custom"),
            (FakeModelPack.Id, true, true, "fp-builtin"),
            ("broken-pack", false, false, "fp-broken"),
        ]);
        var service = NewService(packs, new FakeTagInferenceService(), "");

        var missing = await service.EnsureModelAsync("no-such-pack", TestContext.Current.CancellationToken);
        Assert.Equal("custom-pack", missing.Descriptor.Id);

        var invalid = await service.EnsureModelAsync("broken-pack", TestContext.Current.CancellationToken);
        Assert.Equal("custom-pack", invalid.Descriptor.Id);
    }

    [Trait("Category", "Unit")]
    [Fact(Skip = "Model selection by ID was removed; the application now uses one external directory.")]
    public async Task EnsureModel_falls_back_to_settings_preferred_when_arg_empty()
    {
        var packs = new FakeModelPackService([
            ("custom-pack", false, true, "fp-custom"),
            (FakeModelPack.Id, true, true, "fp-builtin"),
        ]);
        var service = NewService(packs, new FakeTagInferenceService(), "custom-pack");

        var loaded = await service.EnsureModelAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("custom-pack", loaded.Descriptor.Id);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task EnsureModel_no_valid_pack_throws_model_pack_invalid()
    {
        var packs = new FakeModelPackService([
            ("broken-pack", false, false, "fp-broken"),
        ]);
        var service = NewService(packs, new FakeTagInferenceService(), "");

        var exception = await Assert.ThrowsAsync<TaggerException>(() =>
            service.EnsureModelAsync(null, TestContext.Current.CancellationToken));

        Assert.Equal(TaggerErrorCode.ModelPackInvalid, exception.Code);
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }

    [Trait("Category", "Unit")]
    [Fact(Skip = "Model selection by ID was removed; the application now uses one external directory.")]
    public async Task EnsureModel_same_pack_reuses_cached_instance()
    {
        var packs = new FakeModelPackService([
            (FakeModelPack.Id, true, true, "fp-builtin"),
        ]);
        var service = NewService(packs, new FakeTagInferenceService(), "");

        var first = await service.EnsureModelAsync(FakeModelPack.Id, TestContext.Current.CancellationToken);
        var second = await service.EnsureModelAsync(FakeModelPack.Id, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Equal(1, packs.LoadCalls);
    }

    // ---------- RecognizeOneAsync ----------

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeOne_success_writes_back()
    {
        var pack = TestPack("fp-v1");
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            new FakeTagInferenceService(_ => [0.9f, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f]),
            "");
        var image = MakeImage("img-ok");

        var snapshot = await service.RecognizeOneAsync(image, pack, TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        Assert.Same(snapshot, image.Prediction);
        Assert.Equal(AnalysisState.Succeeded, image.AnalysisState);
        Assert.Null(image.LastError);
        Assert.Equal("fp-v1", image.Prediction!.ModelFingerprint);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeOne_failure_is_isolated_with_chinese_message()
    {
        var pack = TestPack("fp-v1");
        var log = new RecordingLogService();
        var inference = new ScriptedInferenceService((_, _, _) =>
            Task.FromException<PredictionSnapshot>(new InvalidOperationException("technical-boom-123")));
        var service = new RecognitionService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            new MemorySettingsStore(),
            log);

        var image = MakeImage("img-bad");
        var snapshot = await service.RecognizeOneAsync(image, pack, TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
        Assert.Equal(AnalysisState.Failed, image.AnalysisState);
        Assert.Null(image.Prediction);
        Assert.False(string.IsNullOrWhiteSpace(image.LastError));
        // 技术细节不暴露：不含原始异常文本与堆栈痕迹。
        Assert.DoesNotContain("technical-boom-123", image.LastError!, StringComparison.Ordinal);
        Assert.DoesNotContain("at ", image.LastError!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\n", image.LastError!, StringComparison.Ordinal);
        Assert.NotEmpty(log.Errors);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeOne_tagger_failure_surfaces_user_message()
    {
        var pack = TestPack("fp-v1");
        var inference = new ScriptedInferenceService((_, _, _) =>
            Task.FromException<PredictionSnapshot>(
                new TaggerException(TaggerErrorCode.InferenceFailed, "推理失败，请重试。")));
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            "");

        var image = MakeImage("img-tagger-bad");
        var snapshot = await service.RecognizeOneAsync(image, pack, TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
        Assert.Equal(AnalysisState.Failed, image.AnalysisState);
        Assert.Equal("推理失败，请重试。", image.LastError);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeOne_canceled_token_does_not_write_back()
    {
        var pack = TestPack("fp-v1");
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            new FakeTagInferenceService(),
            "");
        var image = MakeImage("img-cancel");

        var snapshot = await service.RecognizeOneAsync(
            image, pack, new CancellationToken(canceled: true));

        Assert.Null(snapshot);
        Assert.Equal(AnalysisState.Canceled, image.AnalysisState);
        Assert.Null(image.Prediction);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeOne_tagger_canceled_does_not_write_back()
    {
        var pack = TestPack("fp-v1");
        var inference = new ScriptedInferenceService((_, _, _) =>
            Task.FromException<PredictionSnapshot>(
                new TaggerException(TaggerErrorCode.Canceled, "识别已取消。")));
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            "");
        var image = MakeImage("img-tagger-cancel");

        var snapshot = await service.RecognizeOneAsync(image, pack, TestContext.Current.CancellationToken);

        Assert.Null(snapshot);
        Assert.Equal(AnalysisState.Canceled, image.AnalysisState);
        Assert.Null(image.Prediction);
    }

    // ---------- RecognizeAllAsync ----------

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeAll_skips_succeeded_unexpired()
    {
        var pack = TestPack("fp-v1");
        var fresh = MakeImage("batch-fresh");
        var done = MakeImage("batch-done", AnalysisState.Succeeded);
        done.Prediction = Snapshot(pack, [0.8f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
        var stale = MakeImage("batch-stale", AnalysisState.Succeeded);
        stale.Prediction = Snapshot(pack, [0.1f, 0.9f, 0.1f, 0.1f, 0.1f, 0.1f]) with
        {
            ModelFingerprint = "fp-old",
        };
        var inference = new FakeTagInferenceService(_ => [0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f]);
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            "");

        var result = await service.RecognizeAllAsync(
            [fresh, done, stale], pack, null, TestContext.Current.CancellationToken);

        Assert.Equal(new BatchInferenceResult(2, 0, 1, 0), result);
        Assert.Equal(2, inference.InferCalls);
        // 已成功未过期项保持原结果。
        Assert.Equal(0.8f, done.Prediction!.Probabilities[0], precision: 5);
        Assert.Equal(AnalysisState.Succeeded, done.AnalysisState);
        Assert.Equal("fp-v1", fresh.Prediction!.ModelFingerprint);
        Assert.Equal("fp-v1", stale.Prediction!.ModelFingerprint);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeAll_single_failure_does_not_abort_batch()
    {
        var pack = TestPack("fp-v1");
        var first = MakeImage("batch-first");
        var bad = MakeImage("batch-bad");
        var last = MakeImage("batch-last");
        var inference = new ScriptedInferenceService((doc, p, _) =>
        {
            if (string.Equals(doc.Id, "batch-bad", StringComparison.Ordinal))
                return Task.FromException<PredictionSnapshot>(
                    new TaggerException(TaggerErrorCode.InferenceFailed, "单图识别失败，请重试。"));
            return Task.FromResult(Snapshot(p, [0.6f, 0.4f, 0.3f, 0.2f, 0.1f, 0.05f]));
        });
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            "");
        var progress = new CollectingProgress();

        var result = await service.RecognizeAllAsync(
            [first, bad, last], pack, progress, TestContext.Current.CancellationToken);

        Assert.Equal(new BatchInferenceResult(2, 1, 0, 0), result);
        Assert.Equal(AnalysisState.Succeeded, first.AnalysisState);
        Assert.Equal(AnalysisState.Failed, bad.AnalysisState);
        Assert.Equal("单图识别失败，请重试。", bad.LastError);
        Assert.Equal(AnalysisState.Succeeded, last.AnalysisState);
        Assert.Equal((3, 3), progress.Reports[^1]);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeAll_cancel_does_not_commit_late_results()
    {
        var pack = TestPack("fp-v1");
        var first = MakeImage("late-first");
        var second = MakeImage("late-second");
        using var cancellation = new CancellationTokenSource();
        var inference = new ScriptedInferenceService(async (doc, p, _) =>
        {
            if (string.Equals(doc.Id, "late-first", StringComparison.Ordinal))
            {
                // 模拟原生调用不可中止：忽略令牌完成后由服务丢弃。
                await Task.Delay(10, CancellationToken.None);
                cancellation.Cancel();
                return Snapshot(p, [0.9f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
            }

            return Snapshot(p, [0.1f, 0.9f, 0.1f, 0.1f, 0.1f, 0.1f]);
        });
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            "");

        var result = await service.RecognizeAllAsync(
            [first, second], pack, null, cancellation.Token);

        Assert.Equal(AnalysisState.Canceled, first.AnalysisState);
        Assert.Null(first.Prediction);
        Assert.Equal(AnalysisState.Canceled, second.AnalysisState);
        Assert.Null(second.Prediction);
        Assert.Equal(0, result.Succeeded);
        Assert.True(result.Canceled >= 1);
        // 第二项取消后不再触发推理。
        Assert.Equal(1, inference.Calls);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeAll_progress_counts_each_completion()
    {
        var pack = TestPack("fp-v1");
        var fresh = MakeImage("prog-fresh");
        var done = MakeImage("prog-done", AnalysisState.Succeeded);
        done.Prediction = Snapshot(pack, [0.7f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
        var retry = MakeImage("prog-retry", AnalysisState.Failed);
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            new FakeTagInferenceService(),
            "");
        var progress = new CollectingProgress();

        var result = await service.RecognizeAllAsync(
            [fresh, done, retry], pack, progress, TestContext.Current.CancellationToken);

        Assert.Equal(new BatchInferenceResult(2, 0, 1, 0), result);
        Assert.Equal(3, progress.Reports.Count);
        Assert.Equal((1, 3), progress.Reports[0]);
        Assert.Equal((2, 3), progress.Reports[1]);
        Assert.Equal((3, 3), progress.Reports[2]);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public async Task RecognizeAll_writes_back_by_id_without_crosstalk()
    {
        var pack = TestPack("fp-v1");
        var images = Enumerable.Range(0, 5).Select(i => MakeImage($"cross-{i}")).ToArray();
        var inference = new ScriptedInferenceService(async (doc, p, ct) =>
        {
            int index = int.Parse(doc.Id.Split('-')[1], System.Globalization.CultureInfo.InvariantCulture);
            // 早图慢、晚图快：验证按 Id 回填而非按完成顺序串图。
            await Task.Delay((5 - index) * 10, ct);
            return Snapshot(p, [(index + 1) / 10f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
        });
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-v1")]),
            inference,
            "");

        var result = await service.RecognizeAllAsync(
            images, pack, null, TestContext.Current.CancellationToken);

        Assert.Equal(new BatchInferenceResult(5, 0, 0, 0), result);
        for (int i = 0; i < images.Length; i++)
        {
            Assert.Equal(AnalysisState.Succeeded, images[i].AnalysisState);
            Assert.Equal($"cross-{i}", images[i].Id);
            Assert.Equal((i + 1) / 10f, images[i].Prediction!.Probabilities[0], precision: 5);
        }
    }

    // ---------- MarkStale / 关闭确认 ----------

    [Trait("Category", "Unit")]
    [Fact]
    public void MarkStale_only_marks_expired_succeeded()
    {
        var service = NewService(
            new FakeModelPackService([(FakeModelPack.Id, true, true, "fp-new")]),
            new FakeTagInferenceService(),
            "");
        var current = MakeImage("stale-current", AnalysisState.Succeeded);
        current.Prediction = new PredictionSnapshot(
            "fp-new", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1), "test", "CPU", 1,
            [0.9f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);
        var expired = MakeImage("stale-expired", AnalysisState.Succeeded);
        expired.Prediction = new PredictionSnapshot(
            "fp-old", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1), "test", "CPU", 1,
            [0.8f, 0.2f, 0.1f, 0.1f, 0.1f, 0.1f]);
        var unrecognized = MakeImage("stale-none", AnalysisState.NotRun);
        var failed = MakeImage("stale-failed", AnalysisState.Failed);
        failed.Prediction = new PredictionSnapshot(
            "fp-old", DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1), "test", "CPU", 1,
            [0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.1f]);

        service.MarkStaleForModelChange([current, expired, unrecognized, failed], "fp-new");

        Assert.Equal(AnalysisState.Succeeded, current.AnalysisState);
        Assert.Equal(AnalysisState.Stale, expired.AnalysisState);
        // 旧结果保留。
        Assert.NotNull(expired.Prediction);
        Assert.Equal("fp-old", expired.Prediction!.ModelFingerprint);
        // 未识别与失败项不动。
        Assert.Equal(AnalysisState.NotRun, unrecognized.AnalysisState);
        Assert.Equal(AnalysisState.Failed, failed.AnalysisState);
    }

    [Trait("Category", "Unit")]
    [Fact]
    public void ShouldConfirmExit_returns_running_flag()
    {
        Assert.True(RecognitionService.ShouldConfirmExit(true));
        Assert.False(RecognitionService.ShouldConfirmExit(false));
    }

    // ---------- 测试本地假服务与帮助方法 ----------

    private static RecognitionService NewService(
        FakeModelPackService packs, ITagInferenceService inference, string settingsPackId) =>
        new(packs, inference, new MemorySettingsStore(new AppSettings { ModelPackPath = settingsPackId }));

    private static ImageDocument MakeImage(string id, AnalysisState state = AnalysisState.NotRun) => new()
    {
        Id = id,
        CanonicalPath = Path.Combine(Path.GetTempPath(), $"{id}.png"),
        FileName = $"{id}.png",
        FileSize = 10,
        Format = "png",
        PixelWidth = 8,
        PixelHeight = 8,
        AnalysisState = state,
    };

    private static LoadedModelPack TestPack(string fingerprintValue, string id = "test-pack")
    {
        var descriptor = FakeModelPack.Descriptor(id);
        var fingerprint = FakeModelPack.Fingerprint(id) with { Value = fingerprintValue };
        var pipeline = new CompiledPreprocessingPipeline
        {
            Fingerprint = fingerprint.Value,
            PerSampleTensorContract = new ModelInputTensor
            {
                DType = TensorDType.Float32,
                Shape = [3, descriptor.Input.Width, descriptor.Input.Height],
                Layout = "NCHW",
            },
            Steps = [],
            Operators = [],
        };
        return new LoadedModelPack(descriptor, FakeModelPack.Catalog(), fingerprint, pipeline);
    }

    private static PredictionSnapshot Snapshot(LoadedModelPack pack, float[] probabilities) =>
        new(pack.Fingerprint.Value, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1), "test", "CPU", 1, probabilities);

    /// <summary>测试本地模型包桩：Discover 返回内存条目，LoadAsync 按需组装已加载包。</summary>
    private sealed class FakeModelPackService : IModelPackService
    {
        private readonly List<ModelPackInfo> _packs;
        private readonly Dictionary<string, string> _fingerprints;

        public FakeModelPackService(IEnumerable<(string Id, bool IsBuiltIn, bool IsValid, string Fingerprint)> packs)
        {
            _packs = [];
            _fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (id, isBuiltIn, isValid, fingerprint) in packs)
            {
                var descriptor = FakeModelPack.Descriptor(id);
                _packs.Add(new ModelPackInfo(
                    descriptor,
                    isBuiltIn,
                    isValid,
                    isValid ? null : "测试桩标记为无效。"));
                _fingerprints[id] = fingerprint;
            }
        }

        public int LoadCalls { get; private set; }

        public IReadOnlyList<ModelPackInfo> Discover() => _packs;

        public Task<LoadedModelPack> LoadAsync(string modelPackId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = _packs.FirstOrDefault(pack =>
                string.Equals(pack.Descriptor.Id, modelPackId, StringComparison.Ordinal));
            if (info is null)
                throw new TaggerException(TaggerErrorCode.ModelPackInvalid, "测试桩中不存在该模型包。");
            if (!info.IsValid)
                throw new TaggerException(TaggerErrorCode.ModelPackCorrupt, "测试桩模型包无效。");

            LoadCalls++;
            var fingerprint = FakeModelPack.Fingerprint(modelPackId) with
            {
                Value = _fingerprints[modelPackId],
            };
            var pipeline = new CompiledPreprocessingPipeline
            {
                Fingerprint = fingerprint.Value,
                PerSampleTensorContract = new ModelInputTensor
                {
                    DType = TensorDType.Float32,
                    Shape = [3, info.Descriptor.Input.Width, info.Descriptor.Input.Height],
                    Layout = "NCHW",
                },
                Steps = [],
                Operators = [],
            };
            return Task.FromResult(new LoadedModelPack(
                info.Descriptor, FakeModelPack.Catalog(), fingerprint, pipeline));
        }

        public Task<ModelDescriptor> InstallAsync(string itmodelPath, CancellationToken cancellationToken) =>
            throw new NotSupportedException("测试桩不支持安装。");

        public void Uninstall(string modelPackId)
        {
        }
    }

    /// <summary>内存设置桩：只存取 <see cref="AppSettings"/>，不碰文件系统。</summary>
    private sealed class MemorySettingsStore : ISettingsStore
    {
        private AppSettings _settings;

        public MemorySettingsStore(AppSettings? initial = null) =>
            _settings = initial ?? new AppSettings();

        public AppSettings Load() => _settings;

        public void Save(AppSettings settings) => _settings = settings;
    }

    /// <summary>可编排的推理桩：按回调返回快照或抛错，并计数调用。</summary>
    private sealed class ScriptedInferenceService(
        Func<ImageDocument, LoadedModelPack, CancellationToken, Task<PredictionSnapshot>> func)
        : ITagInferenceService
    {
        private readonly Func<ImageDocument, LoadedModelPack, CancellationToken, Task<PredictionSnapshot>> _func = func;

        public int Calls { get; private set; }

        public bool IsLoaded(ModelPackFingerprint fingerprint) => true;

        public Task<PredictionSnapshot> InferAsync(
            ImageDocument image, LoadedModelPack pack, CancellationToken cancellationToken)
        {
            Calls++;
            return _func(image, pack, cancellationToken);
        }
    }

    /// <summary>记录型日志桩：不断言路径与原文，只收集错误供验证。</summary>
    private sealed class RecordingLogService : ILogService
    {
        public List<(string Message, Exception? Exception)> Errors { get; } = [];

        public void Info(string message)
        {
        }

        public void Warning(string message)
        {
        }

        public void Error(string message, Exception? exception = null) =>
            Errors.Add((message, exception));

        public void ErrorSuppressed(string dedupKey, string message, Exception? exception = null) =>
            Errors.Add((message, exception));

        public void LogInference(string modelFingerprint, string provider, string device, double durationMs)
        {
        }
    }

    private sealed class CollectingProgress : IProgress<(int Completed, int Total)>
    {
        public List<(int Completed, int Total)> Reports { get; } = [];

        public void Report((int Completed, int Total) value) => Reports.Add(value);
    }
}
