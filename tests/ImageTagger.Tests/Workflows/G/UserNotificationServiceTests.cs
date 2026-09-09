using ImageTagger.App.Resources;
using ImageTagger.App.Services;
using ImageTagger.Core;
using ImageTagger.Infrastructure.Logging;
using ImageTagger.Tests.TestInfrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

[Trait("Category", TestCategories.Unit)]
public sealed class UserNotificationServiceTests
{
    [Fact]
    public void Error_codes_map_to_understandable_chinese_conclusions()
    {
        Assert.Equal(Strings.Error_ImageCorrupt, TaggerErrorMessages.GetConclusion(TaggerErrorCode.ImageCorrupt));
        Assert.Equal(Strings.Error_OutOfMemory, TaggerErrorMessages.GetConclusion(TaggerErrorCode.OutOfMemory));
        Assert.Equal(Strings.Error_ModelPackInvalid, TaggerErrorMessages.GetConclusion(TaggerErrorCode.ModelPackInvalid));
    }

    [Fact]
    public async Task Non_blocking_error_shows_toast_and_logs_details()
    {
        var sink = new RecordingSink();
        using var serilog = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using var log = new SerilogFileLogger(serilog);
        var service = new UserNotificationService(log);

        await service.ShowErrorAsync(new TaggerException(TaggerErrorCode.ImageCorrupt, "bad bytes"), TestContext.Current.CancellationToken);

        var toast = Assert.Single(service.ToastHistory);
        Assert.Contains(Strings.Error_ImageCorrupt, toast.Title, StringComparison.Ordinal);
        Assert.Contains(sink.Events, e => e.Level == Serilog.Events.LogEventLevel.Error);
    }

    [Fact]
    public void Duplicate_batch_errors_merge_into_one_summary_toast()
    {
        using var log = new SerilogFileLogger(
            new LoggerConfiguration().WriteTo.Sink(new RecordingSink()).CreateLogger());
        var service = new UserNotificationService(log);

        service.ShowBatchErrors(
        [
            new TaggerException(TaggerErrorCode.ImageCorrupt, "a"),
            new TaggerException(TaggerErrorCode.ImageCorrupt, "b"),
            new TaggerException(TaggerErrorCode.OutOfMemory, "c"),
        ]);

        var toast = Assert.Single(service.ToastHistory);
        Assert.Equal(Strings.Toast_BatchErrorTitle, toast.Title);
        Assert.Contains("3", toast.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirm_dialogs_record_history_and_return_handler_result()
    {
        var service = new UserNotificationService(
            null,
            toastHandler: (_, _) => { },
            dialogHandler: (_, _, _) => Task.FromResult(true));

        Assert.True(await service.ConfirmClearSessionAsync(TestContext.Current.CancellationToken));
        Assert.True(await service.ConfirmOverwriteAsync("prompt.txt", TestContext.Current.CancellationToken));
        Assert.True(await service.ConfirmLargeImageAsync("huge.png", 250_000_000, TestContext.Current.CancellationToken));
        Assert.Equal(3, service.DialogHistory.Count);
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
