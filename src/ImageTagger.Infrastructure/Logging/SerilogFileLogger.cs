using Serilog;

namespace ImageTagger.Infrastructure.Logging;

/// <summary>
/// 基于 Serilog 滚动文件的 <see cref="ILogService"/> 实现（DESIGN 18.2 / TASKS G-05）。
/// 隐私：默认只记文件名、ModelPackId 与 hash 前缀，不记完整路径、Prompt、元数据或图片内容。
/// 合并：同 key 在 5 秒抑制窗口内只写一次，其余计数后在下一次窗口合并输出。
/// </summary>
public sealed class SerilogFileLogger : ILogService, IDisposable
{
    /// <summary>重复批量错误的抑制窗口。</summary>
    public static readonly TimeSpan SuppressionWindow = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly Serilog.ILogger _logger;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, SuppressionState> _suppressions = new(StringComparer.Ordinal);
    private bool _disposed;

    public SerilogFileLogger(Serilog.ILogger logger, Func<DateTimeOffset>? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>只保留文件名，用于日志中的图片与模型文件引用。</summary>
    public static string SanitizeFileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        try
        {
            return Path.GetFileName(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// 路径脱敏：只保留 <c>ModelPackId/文件名#hash前8</c>，
    /// 绝不输出完整绝对路径。
    /// </summary>
    public static string SanitizePath(string modelPackId, string? path, string? hash)
    {
        var safeId = string.IsNullOrWhiteSpace(modelPackId) ? "unknown-pack" : modelPackId;
        var fileName = SanitizeFileName(path);
        if (string.IsNullOrEmpty(fileName))
            fileName = "unknown-file";
        var shortHash = string.IsNullOrEmpty(hash)
            ? "unknown"
            : hash.Length <= 8 ? hash : hash.Substring(0, 8);
        return string.Concat(safeId, "/", fileName, "#", shortHash);
    }

    public void Info(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        _logger.Information("{Message}", message);
    }

    public void Warning(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        _logger.Warning("{Message}", message);
    }

    public void Error(string message, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();
        if (exception is null)
            _logger.Error("{Message}", message);
        else
            _logger.Error(exception, "{Message}", message);
    }

    public void ErrorSuppressed(string dedupKey, string message, Exception? exception = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dedupKey);
        ArgumentNullException.ThrowIfNull(message);
        ThrowIfDisposed();

        string toWrite;
        Exception? toWriteException;
        lock (_gate)
        {
            var now = _clock();
            if (_suppressions.TryGetValue(dedupKey, out var state)
                && now - state.LastWrite < SuppressionWindow)
            {
                state.PendingCount++;
                return;
            }

            toWrite = state is not null && state.PendingCount > 0
                ? string.Concat(message, "（另有 ", state.PendingCount.ToString(), " 条重复已合并）")
                : message;
            toWriteException = exception;
            _suppressions[dedupKey] = new SuppressionState(now, 0);
        }

        if (toWriteException is null)
            _logger.Error("{Message}", toWrite);
        else
            _logger.Error(toWriteException, "{Message}", toWrite);
    }

    public void LogInference(string modelFingerprint, string provider, string device, double durationMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(device);
        ThrowIfDisposed();
        _logger.Information(
            "Inference {ModelFingerprint} {Provider} {Device} {DurationMs}ms",
            modelFingerprint,
            provider,
            device,
            durationMs);
    }

    /// <summary>返回某 key 当前被抑制的待合并计数（测试用）。</summary>
    public int GetPendingCount(string dedupKey)
    {
        lock (_gate)
            return _suppressions.TryGetValue(dedupKey, out var state) ? state.PendingCount : 0;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_logger is IDisposable disposable)
            disposable.Dispose();
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SerilogFileLogger));
    }

    private sealed class SuppressionState(DateTimeOffset lastWrite, int pendingCount)
    {
        public DateTimeOffset LastWrite { get; set; } = lastWrite;

        public int PendingCount { get; set; } = pendingCount;
    }
}
