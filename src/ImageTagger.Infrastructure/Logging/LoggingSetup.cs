using System.Text;
using Serilog;

namespace ImageTagger.Infrastructure.Logging;

/// <summary>
/// Serilog 滚动文件配置（DESIGN 15 / 18.2，TASKS G-05）：
/// <c>logs/image-tagger-*.log</c> 每日滚动、保留 7 天、单文件上限 100 MiB，
/// 超限滚动且只保留最近文件，总量有界。
/// </summary>
public static class LoggingSetup
{
    /// <summary>滚动文件保留数量（7 天）。</summary>
    public const int RetainedFileCountLimit = 7;

    /// <summary>滚动文件保留天数。</summary>
    public const int RetainedDays = 7;

    /// <summary>单文件上限 100 MiB。</summary>
    public const long FileSizeLimitBytes = 100L * 1024 * 1024;

    /// <summary>滚动文件名模式（Serilog Day 滚动后形如 image-tagger20260101.log）。</summary>
    public const string LogFileNamePattern = "image-tagger-.log";

    /// <summary>每日滚动间隔。</summary>
    public const RollingInterval RollingInterval = Serilog.RollingInterval.Day;

    /// <summary>返回滚动文件的完整模式路径（目录 + 模式名）。</summary>
    public static string GetLogFilePath(string logsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        return Path.Combine(logsRoot, LogFileNamePattern);
    }

    /// <summary>按平台日志目录创建滚动文件 Logger。</summary>
    public static Serilog.ILogger CreateLogger(string logsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsRoot);
        Directory.CreateDirectory(logsRoot);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.WithProperty("App", "ImageTagger")
            .WriteTo.File(
                GetLogFilePath(logsRoot),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCountLimit,
                retainedFileTimeLimit: TimeSpan.FromDays(RetainedDays),
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                encoding: Encoding.UTF8,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    /// <summary>创建生产用的 <see cref="ILogService"/>（文件滚动 + 5 秒合并抑制）。</summary>
    public static ILogService CreateLogService(string logsRoot, Func<DateTimeOffset>? clock = null) =>
        new SerilogFileLogger(CreateLogger(logsRoot), clock);
}
