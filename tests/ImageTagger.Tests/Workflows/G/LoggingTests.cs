using Serilog;
using Serilog.Core;
using Serilog.Events;
using ImageTagger.Infrastructure.Logging;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

[Trait("Category", TestCategories.Unit)]
public sealed class LoggingTests
{
    [Fact]
    public void Sanitize_helpers_never_emit_full_paths()
    {
        using var temp = new TempDirectory("logging-sanitize");
        var nested = temp.CreateSubdirectory(Path.Combine("sub", "inner"));
        var full = Path.Combine(nested, "photo.png");

        var fileName = SerilogFileLogger.SanitizeFileName(full);
        Assert.Equal("photo.png", fileName);
        Assert.DoesNotContain(temp.FullPath, fileName, StringComparison.Ordinal);

        var hash = new string('a', 64);
        var sanitized = SerilogFileLogger.SanitizePath("my-pack", full, hash);
        Assert.Contains("my-pack", sanitized, StringComparison.Ordinal);
        Assert.Contains("photo.png", sanitized, StringComparison.Ordinal);
        Assert.Contains(hash.Substring(0, 8), sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.FullPath, sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Duplicate_errors_are_suppressed_within_five_seconds()
    {
        var sink = new RecordingSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var now = DateTimeOffset.UtcNow;
        using var logger = new SerilogFileLogger(serilog, () => now);

        logger.ErrorSuppressed("batch-key", "decode failed");
        logger.ErrorSuppressed("batch-key", "decode failed");
        logger.ErrorSuppressed("batch-key", "decode failed");

        Assert.Single(sink.Events);
        Assert.Equal(2, logger.GetPendingCount("batch-key"));

        now += TimeSpan.FromSeconds(6);
        logger.ErrorSuppressed("batch-key", "decode failed");

        Assert.Equal(2, sink.Events.Count);
        Assert.Contains("合并", sink.Events[1].RenderMessage(), StringComparison.Ordinal);
        Assert.Equal(0, logger.GetPendingCount("batch-key"));
    }

    [Fact]
    public void Rolling_configuration_matches_design()
    {
        Assert.Equal(7, LoggingSetup.RetainedFileCountLimit);
        Assert.Equal(7, LoggingSetup.RetainedDays);
        Assert.Equal(100L * 1024 * 1024, LoggingSetup.FileSizeLimitBytes);
        Assert.Equal(RollingInterval.Day, LoggingSetup.RollingInterval);
        Assert.EndsWith("image-tagger-.log", LoggingSetup.GetLogFilePath("logs"), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_logger_ensures_directory_and_inference_log_is_structured()
    {
        using var temp = new TempDirectory("logging-setup");
        var logsRoot = Path.Combine(temp.FullPath, "logs");
        var sink = new RecordingSink();
        using var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        using var logger = new SerilogFileLogger(serilog);

        logger.LogInference("fingerprint-123", "ORT", "CPU", 12.5);

        var message = Assert.Single(sink.Events).RenderMessage();
        Assert.Contains("fingerprint-123", message, StringComparison.Ordinal);
        Assert.Contains("ORT", message, StringComparison.Ordinal);
        Assert.Contains("CPU", message, StringComparison.Ordinal);

        var fileLogger = LoggingSetup.CreateLogger(logsRoot);
        Assert.True(Directory.Exists(logsRoot));
        (fileLogger as IDisposable)?.Dispose();
    }

    private sealed class RecordingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent)
        {
            lock (Events)
                Events.Add(logEvent);
        }
    }
}
