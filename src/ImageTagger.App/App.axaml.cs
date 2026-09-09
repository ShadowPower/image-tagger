using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ImageTagger.App.Composition;
using ImageTagger.App.Services;
using ImageTagger.App.ViewModels;
using ImageTagger.Core;
using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Logging;
using Microsoft.Extensions.DependencyInjection;
using ShadUI;

namespace ImageTagger.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var themeWatcher = new ThemeWatcher(this);
            themeWatcher.Initialize();
            var window = new MainWindow();

            // U-01 Composition Root：唯一一次 ServiceProvider 构建与解析，不用 Service Locator。
            var services = new ServiceCollection();
            services.AddSingleton(themeWatcher);
            services.AddImageTaggerServices();
            services.AddDesktopPlatformService(window);
            var provider = services.BuildServiceProvider();
            var viewModel = provider.GetRequiredService<MainViewModel>();
            window.DataContext = viewModel;

            var packs = provider.GetRequiredService<IModelPackService>();
            var inference = provider.GetRequiredService<ITagInferenceService>();
            var settingsStore = provider.GetRequiredService<ISettingsStore>();
            var promptStore = provider.GetRequiredService<IPromptSettingsStore>();
            var notifications = provider.GetRequiredService<IUserNotificationService>();
            var log = provider.GetRequiredService<ILogService>();
            if (notifications is UserNotificationService concreteNotifications)
            {
                // 主窗口 Hosts 已绑定 viewModel 的管理器；此处把同一实例交给通知服务，
                // Toast/Dialog 即可经 ShadUI 宿主展示，历史记录仍保留以便测试。
                concreteNotifications.BindToastManager(viewModel.ToastManager);
                concreteNotifications.BindDialogManager(viewModel.DialogManager);
            }
            var recognition = new RecognitionService(packs, inference, settingsStore, log);

            CancellationTokenSource? recognitionCancellation = null;
            var sync = new object();

            // 设置与主题：启动时恢复已保存的主题与窗口布局偏好。
            try
            {
                var stored = settingsStore.Load();
                viewModel.SelectedThemeIndex = stored.Theme switch
                {
                    ThemePreference.Light => 1,
                    ThemePreference.Dark => 2,
                    _ => 0,
                };
                viewModel.RecursiveFolderScan = stored.RecursiveFolderScan;
            }
            catch (Exception)
            {
                // 损坏设置已由 SettingsStore 恢复默认；此处保持默认主题继续启动。
            }

            viewModel.SettingsRequested += (_, _) =>
            {
                var settingsViewModel = provider.GetRequiredService<SettingsViewModel>();
                settingsViewModel.ClearSessionPredictions = () =>
                {
                    foreach (var item in viewModel.Images)
                    {
                        item.Document.Prediction = null;
                        item.Document.AnalysisState = AnalysisState.NotRun;
                        item.Document.LastError = null;
                        item.RefreshAnalysisState(viewModel.ThresholdPercent / 100);
                    }
                    viewModel.SyncChildViewModels();
                };
                settingsViewModel.ClearThumbnailCache = () =>
                {
                    if (provider.GetService<IThumbnailService>() is ImageTagger.Infrastructure.Imaging.ThumbnailService thumbnails)
                        thumbnails.ClearCache();
                };
                // 设置页主题即时生效：复用主窗口已有的 ThemeWatcher 链路（SelectedThemeIndex → SwitchTheme）。
                settingsViewModel.ApplyTheme = theme => viewModel.SelectedThemeIndex = theme switch
                {
                    ThemePreference.Light => 1,
                    ThemePreference.Dark => 2,
                    _ => 0,
                };
                settingsViewModel.ApplyChineseTranslation = value =>
                {
                    viewModel.Tags.ShowChineseTranslation = value;
                    viewModel.SyncChildViewModels();
                };
                settingsViewModel.ApplyRecursiveFolderScan = value => viewModel.RecursiveFolderScan = value;
                settingsViewModel.ApplyModelPackPath = path =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        viewModel.CurrentModelName = "未配置模型，请在设置中配置";
                        viewModel.CurrentModelId = null;
                    }
                    else
                    {
                        try
                        {
                            var descriptor = packs.ReadDescriptorFromPath(path);
                            viewModel.CurrentModelName = descriptor.DisplayName;
                            viewModel.StatusBarText = $"模型已配置：{descriptor.DisplayName}";
                            // Force the next recognition to rebuild catalog/context.
                            viewModel.CurrentModelId = null;
                        }
                        catch { viewModel.CurrentModelName = "模型包不可用"; viewModel.StatusBarText = "模型包不可用，请重新选择目录"; }
                    }
                };
                settingsViewModel.ConfirmClearLocalData = token => notifications.ShowDialogAsync(
                    "清理本地数据",
                    "将删除设置、Prompt 规则、缓存、日志和应用托管的模型。用户图片和你选择的外部模型目录不会被删除。继续吗？",
                    cancellationToken: token);
                var dialog = new SettingsWindow { DataContext = settingsViewModel };
                dialog.Show(window);
            };

            viewModel.CancelRequested += (_, _) =>
            {
                lock (sync)
                {
                    recognitionCancellation?.Cancel();
                }
            };

            viewModel.RecognitionRequested += (_, e) =>
            {
                CancellationTokenSource operation;
                lock (sync)
                {
                    recognitionCancellation?.Cancel();
                    recognitionCancellation?.Dispose();
                    recognitionCancellation = new CancellationTokenSource();
                    operation = recognitionCancellation;
                }
                _ = RunRecognitionAsync(
                    viewModel, recognition, promptStore, settingsStore, notifications, log, e, operation.Token);
            };

            // 模型异步加载，不阻塞首屏（DESIGN 19：空载窗口先可交互）。
            _ = InitializeModelAsync(viewModel, recognition, promptStore, settingsStore, log);

            window.Closed += (_, _) =>
            {
                lock (sync)
                {
                    recognitionCancellation?.Cancel();
                    recognitionCancellation?.Dispose();
                    recognitionCancellation = null;
                }
                if (viewModel.CancelCommand.CanExecute(null))
                    viewModel.CancelCommand.Execute(null);
                viewModel.Dispose();
                if (provider is IDisposable providerDisposable)
                    providerDisposable.Dispose();
            };
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) =>
            {
                // 应用关闭时运行中任务按 U-02 取消：此处直接取消，Dialog 确认由窗口关闭流程承载。
                lock (sync)
                {
                    recognitionCancellation?.Cancel();
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 线程模型：服务调用（模型加载、预处理、ONNX 推理）一律在线程池执行，
    /// UI 线程只做轻量状态更新。违反会导致界面假死（DESIGN 19）。
    /// </summary>
    private static Dispatcher UiDispatcher => Dispatcher.UIThread;

    /// <summary>在界面线程执行轻量 UI 更新；已在界面线程则直接执行。</summary>
    private static Task OnUiAsync(Action action)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try
            {
                action();
                completion.SetResult(null);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    /// <summary>在界面线程计算并取值（选择列表项等线程亲和读取）。</summary>
    private static Task<T> OnUiAsync<T>(Func<T> func)
    {
        var dispatcher = UiDispatcher;
        if (dispatcher.CheckAccess())
            return Task.FromResult(func());
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        dispatcher.Post(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    private static async Task InitializeModelAsync(
        MainViewModel viewModel,
        RecognitionService recognition,
        IPromptSettingsStore promptStore,
        ISettingsStore settingsStore,
        ILogService log)
    {
        await OnUiAsync(() => viewModel.CurrentModelName = "正在加载模型…").ConfigureAwait(false);
        try
        {
            // 含 858MB 哈希与 Session 创建，绝不在 UI 线程执行。
            var pack = await Task
                .Run(() => recognition.EnsureModelAsync(null, CancellationToken.None))
                .ConfigureAwait(false);
            var promptSettings = promptStore.Load();
            await OnUiAsync(() =>
            {
                viewModel.SetModelContext(pack.Catalog, pack.Descriptor.Groups, promptSettings, pack.Descriptor.DefaultThreshold);
                viewModel.CurrentModelName = pack.Descriptor.DisplayName;
                viewModel.CurrentModelId = pack.Descriptor.Id;
                viewModel.StatusBarText = $"模型已就绪：{pack.Descriptor.DisplayName}";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Error("内置模型加载失败。", exception);
            var message = exception is TaggerException tagger
                ? tagger.Message
                : "内置模型损坏或缺失，请重装应用或在设置中校验模型";
            await OnUiAsync(() =>
            {
                var unconfigured = exception is TaggerException tagger
                    && tagger.Message.Contains("未配置模型", StringComparison.Ordinal);
                string? currentConfiguredPath = null;
                try { currentConfiguredPath = settingsStore.Load().ModelPackPath; }
                catch { }
                if (unconfigured && !string.IsNullOrWhiteSpace(currentConfiguredPath))
                    return;
                viewModel.CurrentModelName = unconfigured
                    ? "未配置模型，请在设置中配置"
                    : "模型不可用";
                viewModel.StatusBarText = message;
                viewModel.Phase = unconfigured ? WindowPhase.Empty : WindowPhase.Error;
            }).ConfigureAwait(false);
        }
    }

    private static async Task RunRecognitionAsync(
        MainViewModel viewModel,
        RecognitionService recognition,
        IPromptSettingsStore promptStore,
        ISettingsStore settingsStore,
        IUserNotificationService notifications,
        ILogService log,
        RecognitionRequestedEventArgs request,
        CancellationToken cancellationToken)
    {
        LoadedModelPack pack;
        try
        {
            pack = await Task
                .Run(() => recognition.EnsureModelAsync(null, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() => viewModel.EndBatch(canceled: true)).ConfigureAwait(false);
            return;
        }
        catch (Exception exception)
        {
            log.Error("识别前模型准备失败。", exception);
            var message = exception is TaggerException tagger ? tagger.Message : "模型不可用，请重试。";
            await OnUiAsync(() => viewModel.EndBatch(canceled: true)).ConfigureAwait(false);
            await OnUiAsync(() => viewModel.StatusBarText = message).ConfigureAwait(false);
            if (exception is TaggerException taggerException)
            {
                try
                {
                    await notifications
                        .ShowErrorAsync(taggerException, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception dialogException)
                {
                    log.Error("错误通知展示失败。", dialogException);
                }
            }
            return;
        }

        // 以下 UI 快照只读一次，后续重活全部在线程池，UI 更新 Post 回界面线程。
        ImageListItemViewModel[]? snapshot;
        try
        {
            snapshot = await OnUiAsync(() =>
            {
                // 模型切换使旧结果过期，重新识别后恢复正常（U-02）。
                if (!string.Equals(viewModel.CurrentModelId, pack.Descriptor.Id, StringComparison.Ordinal))
                {
                    recognition.MarkStaleForModelChange(
                        viewModel.Images.Select(item => item.Document), pack.Fingerprint.Value);
                    viewModel.SetModelContext(pack.Catalog, pack.Descriptor.Groups, promptStore.Load(), pack.Descriptor.DefaultThreshold);
                    viewModel.CurrentModelName = pack.Descriptor.DisplayName;
                    viewModel.CurrentModelId = pack.Descriptor.Id;
                }

                ImageListItemViewModel? single = null;
                if (!request.RecognizeAll && request.ImageId is not null)
                    single = viewModel.Images.FirstOrDefault(item => item.Id == request.ImageId);
                if (!request.RecognizeAll && single is null)
                    return null;
                var targets = request.RecognizeAll
                    ? viewModel.Images.ToArray()
                    : new[] { single! };
                viewModel.BeginBatch(targets.Length);
                return targets;
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log.Error("识别任务准备失败。", exception);
            await OnUiAsync(() => viewModel.EndBatch(canceled: true)).ConfigureAwait(false);
            return;
        }
        if (snapshot is null)
            return;

        var progress = new Progress<(int completed, int total)>(value =>
        {
            if (UiDispatcher.CheckAccess())
                viewModel.ReportBatchProgress(value.completed);
            else
                UiDispatcher.Post(() => viewModel.ReportBatchProgress(value.completed));
        });

        try
        {
            if (request.RecognizeAll)
            {
                var documents = snapshot.Select(item => item.Document).ToArray();
                await Task
                    .Run(() => recognition.RecognizeAllAsync(documents, pack, progress, cancellationToken))
                    .ConfigureAwait(false);
                var threshold = 0.0;
                await OnUiAsync(() =>
                {
                    threshold = viewModel.ThresholdPercent / 100;
                    foreach (var item in snapshot)
                        item.RefreshAnalysisState(threshold);
                    viewModel.ReportBatchProgress(snapshot.Length);
                    viewModel.SyncChildViewModels();
                    viewModel.EndBatch(canceled: false);
                }).ConfigureAwait(false);
            }
            else
            {
                var single = snapshot[0];
                var image = single.Document;
                var result = await Task
                    .Run(() => recognition.RecognizeOneAsync(image, pack, cancellationToken))
                    .ConfigureAwait(false);
                await OnUiAsync(() =>
                {
                    single.RefreshAnalysisState(viewModel.ThresholdPercent / 100);
                    viewModel.ReportBatchProgress(1);
                    if (result is null)
                        viewModel.EndBatch(canceled: true);
                    else
                    {
                        viewModel.SyncChildViewModels();
                        viewModel.EndBatch(canceled: false);
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await OnUiAsync(() => viewModel.EndBatch(canceled: true)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // fire-and-forget 入口绝不允许异常逃逸导致进程崩溃。
            log.Error("识别任务失败。", exception);
            var message = exception is TaggerException tagger ? tagger.Message : "识别失败，请重试。";
            await OnUiAsync(() =>
            {
                viewModel.EndBatch(canceled: true);
                viewModel.StatusBarText = message;
            }).ConfigureAwait(false);
        }
    }
}
