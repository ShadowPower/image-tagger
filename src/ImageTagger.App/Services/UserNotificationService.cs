using Avalonia.Threading;
using ImageTagger.App.Resources;
using ImageTagger.Core;
using ImageTagger.Infrastructure.Logging;
using ShadUI;

namespace ImageTagger.App.Services;

/// <summary>Toast 种类（DESIGN 12.2）。</summary>
public enum NotificationKind
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>对话框结果。</summary>
public enum DialogResult
{
    Canceled,
    Confirmed,
}

/// <summary>可测试的通知抽象（TASKS G-05）。</summary>
public interface IUserNotificationService
{
    void ShowToast(string message);

    void ShowToast(string title, string message);

    Task<bool> ShowDialogAsync(
        string title, string message, string? details = null, CancellationToken cancellationToken = default);

    Task ShowErrorAsync(TaggerException error, CancellationToken cancellationToken = default);

    Task<bool> ConfirmLargeImageAsync(string filePath, long pixelCount, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="TaggerErrorCode"/> 到中文结论的映射：用户看到可理解结论，
/// 技术详情折叠或只写日志（DESIGN 12.2 / 18.1）。
/// </summary>
public static class TaggerErrorMessages
{
    public static string GetConclusion(TaggerErrorCode code) => code switch
    {
        TaggerErrorCode.ModelPackInvalid => Strings.Error_ModelPackInvalid,
        TaggerErrorCode.ModelPackCorrupt => Strings.Error_ModelPackCorrupt,
        TaggerErrorCode.ModelIncompatible => Strings.Error_ModelIncompatible,
        TaggerErrorCode.ImageUnsupported => Strings.Error_ImageUnsupported,
        TaggerErrorCode.ImageCorrupt => Strings.Error_ImageCorrupt,
        TaggerErrorCode.InferenceFailed => Strings.Error_InferenceFailed,
        TaggerErrorCode.OutOfMemory => Strings.Error_OutOfMemory,
        TaggerErrorCode.ProviderUnavailable => Strings.Error_ProviderUnavailable,
        TaggerErrorCode.SettingsCorrupt => Strings.Error_SettingsCorrupt,
        TaggerErrorCode.IoError => Strings.Error_IoError,
        TaggerErrorCode.Canceled => Strings.Error_Canceled,
        _ => Strings.Error_Unknown,
    };
}

/// <summary>
/// 领域/平台错误到 Toast/Dialog/日志的映射（DESIGN 12.2 / 18.1，TASKS G-05）。
/// Toast：复制成功、导出成功、跳过重复、非阻断错误。
/// Dialog：安装校验失败、清空会话确认、覆盖同名文本、超大图片确认。
/// 同批重复错误合并为摘要 Toast。
/// Avalonia 具体 Toast host 接线见 TODO，ViewModel 只依赖本接口。
/// </summary>
public sealed class UserNotificationService : IUserNotificationService
{
    private static readonly TimeSpan BatchSuppressionWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly ILogService? _log;
    private readonly Action<string, string>? _toastHandler;
    private readonly Func<string, string, string?, Task<bool>>? _dialogHandler;
    private readonly Func<DateTimeOffset> _clock;
    private ToastManager? _toastManager;
    private DialogManager? _dialogManager;
    private string? _lastBatchSummary;
    private DateTimeOffset _lastBatchAt;

    public UserNotificationService(
        ILogService? log = null,
        Action<string, string>? toastHandler = null,
        Func<string, string, string?, Task<bool>>? dialogHandler = null,
        Func<DateTimeOffset>? clock = null)
    {
        _log = log;
        _toastHandler = toastHandler;
        _dialogHandler = dialogHandler;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>测试可见的 Toast 历史。</summary>
    public List<(string Title, string Message)> ToastHistory { get; } = [];

    /// <summary>测试可见的 Dialog 历史。</summary>
    public List<(string Title, string Message, string? Details)> DialogHistory { get; } = [];

    /// <summary>
    /// 接入主窗口的 ShadUI <see cref="ToastManager"/>；测试/无宿主时保持 null，仅记录历史。
    /// 线程安全，可在 Composition Root 调用一次。
    /// </summary>
    public void BindToastManager(ToastManager? toastManager)
    {
        lock (_gate)
            _toastManager = toastManager;
    }

    /// <summary>
    /// 接入主窗口的 ShadUI <see cref="DialogManager"/>；测试可继续用 dialogHandler 注入。
    /// </summary>
    public void BindDialogManager(DialogManager? dialogManager)
    {
        lock (_gate)
            _dialogManager = dialogManager;
    }

    public void ShowToast(string message) => ShowToast(string.Empty, message);

    public void ShowToast(string title, string message)
    {
        ShowToastInternal(title, message, NotificationKind.Info);
    }

    private void ShowToastInternal(string title, string message, NotificationKind kind)
    {
        ArgumentNullException.ThrowIfNull(message);
        title ??= string.Empty;
        ToastManager? toastManager;
        lock (_gate)
        {
            ToastHistory.Add((title, message));
            toastManager = _toastManager;
        }
        _log?.Info(string.IsNullOrEmpty(title) ? message : string.Concat(title, "：", message));

        _toastHandler?.Invoke(title, message);
        if (toastManager is null)
            return;

        // ShadUI Toast 必须在 UI 线程创建；后台线程经 Dispatcher 转发。
        void Show()
        {
            try
            {
                var builder = string.IsNullOrEmpty(title)
                    ? toastManager.CreateToast(message)
                    : toastManager.CreateToast(title).WithContent(message);
                builder.WithDelay(4).DismissOnClick();
                switch (kind)
                {
                    case NotificationKind.Success:
                        builder.ShowSuccess();
                        break;
                    case NotificationKind.Warning:
                        builder.ShowWarning();
                        break;
                    case NotificationKind.Error:
                        builder.ShowError();
                        break;
                    default:
                        builder.ShowInfo();
                        break;
                }
            }
            catch (Exception exception)
            {
                _log?.Error("Toast 展示失败。", exception);
            }
        }

        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
            Show();
        else
            dispatcher.Post(Show);
    }

    public async Task<bool> ShowDialogAsync(
        string title, string message, string? details = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(message);
        DialogManager? dialogManager;
        lock (_gate)
        {
            DialogHistory.Add((title, message, details));
            dialogManager = _dialogManager;
        }
        _log?.Info(string.Concat(title, "：", message));

        if (_dialogHandler is not null)
            return await _dialogHandler(title, message, details).ConfigureAwait(false);

        // 生产路径走 ShadUI DialogHost；无宿主（单测/Headless）时返回取消，避免误做破坏性操作。
        if (dialogManager is null)
            return false;

        var fullMessage = string.IsNullOrEmpty(details)
            ? message
            : string.Concat(message, "\n\n", details);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            try
            {
                dialogManager
                    .CreateDialog(title, fullMessage)
                    .WithPrimaryButton("确认", () => completion.TrySetResult(true))
                    .WithCancelButton("取消", () => completion.TrySetResult(false))
                    .Dismissible()
                    .WithMaxWidth(480)
                    .Show();
            }
            catch (Exception exception)
            {
                _log?.Error("确认对话框展示失败。", exception);
                completion.TrySetResult(false);
            }
        }

        var dispatcher = Dispatcher.UIThread;
        if (dispatcher.CheckAccess())
            Show();
        else
            dispatcher.Post(Show);

        using (cancellationToken.Register(() => completion.TrySetResult(false)))
            return await completion.Task.ConfigureAwait(false);
    }

    public Task ShowErrorAsync(TaggerException error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        var conclusion = TaggerErrorMessages.GetConclusion(error.Code);
        var details = error.Message;
        _log?.Error(string.Concat(conclusion, "：", details), error.InnerException);
        ShowToastInternal(conclusion, details, NotificationKind.Error);
        return Task.CompletedTask;
    }

    /// <summary>复制成功 Toast。</summary>
    public void NotifyCopySuccess() => ShowToastInternal(Strings.Toast_CopySuccess, Strings.Toast_CopySuccess, NotificationKind.Success);

    /// <summary>导出成功 Toast（只记文件名）。</summary>
    public void NotifyExportSuccess(string filePath) => ShowToastInternal(
        Strings.Toast_ExportSuccess,
        SerilogFileLogger.SanitizeFileName(filePath),
        NotificationKind.Success);

    /// <summary>跳过重复文件 Toast。</summary>
    public void NotifySkippedDuplicates(int count)
    {
        if (count <= 0)
            return;
        ShowToastInternal(Strings.Toast_SkippedDuplicates, string.Concat(Strings.Toast_SkippedDuplicates, " ", count.ToString(), " 项"), NotificationKind.Warning);
    }

    /// <summary>非阻断错误 Toast + 日志详情。</summary>
    public void NotifyNonBlockingError(TaggerException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var conclusion = TaggerErrorMessages.GetConclusion(error.Code);
        _log?.Error(string.Concat(conclusion, "：", error.Message), error.InnerException);
        ShowToastInternal(conclusion, Strings.Toast_NonBlockingError, NotificationKind.Warning);
    }

    /// <summary>同批重复错误合并为一个摘要 Toast。</summary>
    public void ShowBatchErrors(IReadOnlyList<TaggerException> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
            return;
        if (errors.Count == 1)
        {
            NotifyNonBlockingError(errors[0]);
            return;
        }

        var groups = errors
            .GroupBy(error => error.Code)
            .OrderByDescending(group => group.Count())
            .ToArray();
        string summary = groups.Length == 1
            ? string.Concat(
                TaggerErrorMessages.GetConclusion(groups[0].Key),
                "（共 ",
                errors.Count.ToString(),
                " 项）")
            : string.Concat(
                "批量任务中有 ",
                errors.Count.ToString(),
                " 项失败：",
                string.Join("、", groups.Select(group =>
                    string.Concat(TaggerErrorMessages.GetConclusion(group.Key), " ", group.Count().ToString(), " 项"))));

        lock (_gate)
        {
            var now = _clock();
            if (string.Equals(_lastBatchSummary, summary, StringComparison.Ordinal)
                && now - _lastBatchAt < BatchSuppressionWindow)
            {
                return;
            }

            _lastBatchSummary = summary;
            _lastBatchAt = now;
        }

        foreach (var error in errors)
            _log?.Error(
                string.Concat(TaggerErrorMessages.GetConclusion(error.Code), "：", error.Message),
                error.InnerException);
        ShowToastInternal(Strings.Toast_BatchErrorTitle, summary, NotificationKind.Warning);
    }

    /// <summary>安装校验失败 Dialog（技术详情折叠）。</summary>
    public Task<bool> ReportInstallValidationFailedAsync(string details, CancellationToken cancellationToken = default) =>
        ShowDialogAsync(Strings.Dialog_InstallFailedTitle, Strings.Dialog_InstallFailedTitle, details, cancellationToken);

    /// <summary>清空有结果的会话确认。</summary>
    public Task<bool> ConfirmClearSessionAsync(CancellationToken cancellationToken = default) =>
        ShowDialogAsync(
            Strings.Dialog_ClearSessionTitle,
            Strings.Dialog_ClearSessionMessage,
            null,
            cancellationToken);

    /// <summary>覆盖同名文本确认（只展示文件名）。</summary>
    public Task<bool> ConfirmOverwriteAsync(string filePath, CancellationToken cancellationToken = default) =>
        ShowDialogAsync(
            Strings.Dialog_OverwriteTitle,
            string.Concat(Strings.Dialog_OverwriteMessage, "（", SerilogFileLogger.SanitizeFileName(filePath), "）"),
            null,
            cancellationToken);

    /// <summary>超大图片确认（只展示文件名与像素数）。</summary>
    public Task<bool> ConfirmLargeImageAsync(string filePath, long pixelCount, CancellationToken cancellationToken = default) =>
        ShowDialogAsync(
            Strings.Dialog_LargeImageTitle,
            string.Concat(
                SerilogFileLogger.SanitizeFileName(filePath),
                "（",
                pixelCount.ToString(),
                " 像素）",
                Strings.Dialog_LargeImageMessage),
            null,
            cancellationToken);
}
