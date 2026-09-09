using Avalonia.Threading;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Deterministic polling/timeout helpers so async commands, cancellation and
/// dispatcher work never depend on wall-clock luck inside tests.
/// </summary>
public static class Determinism
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Polls <paramref name="predicate"/> until it returns true or the timeout
    /// elapses; throws a descriptive <see cref="TimeoutException"/> on timeout.
    /// </summary>
    public static async Task WaitForAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? description = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var interval = pollInterval ?? DefaultPollInterval;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Condition not met within {timeout ?? DefaultTimeout}: {description ?? "unnamed condition"}");
            await Task.Delay(interval).ConfigureAwait(false);
        }
    }

    /// <summary>Waits for an async predicate; same polling contract as the sync overload.</summary>
    public static async Task WaitForAsync(
        Func<Task<bool>> predicate,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        string? description = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        var interval = pollInterval ?? DefaultPollInterval;
        while (!await predicate().ConfigureAwait(false))
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"Condition not met within {timeout ?? DefaultTimeout}: {description ?? "unnamed condition"}");
            await Task.Delay(interval).ConfigureAwait(false);
        }
    }

    /// <summary>A cancellation source that fires after <paramref name="timeout"/>.</summary>
    public static CancellationTokenSource CancelAfter(TimeSpan timeout)
        => new(timeout);

    /// <summary>
    /// Drains the Avalonia UI dispatcher queue until empty. Under the headless
    /// platform there is no render loop, so tests must pump explicitly.
    /// </summary>
    public static void PumpDispatcher()
    {
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps the dispatcher repeatedly until <paramref name="predicate"/> holds or timeout.</summary>
    public static async Task PumpUntilAsync(
        Func<bool> predicate,
        TimeSpan? timeout = null,
        string? description = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (!predicate())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException(
                    $"UI condition not met within {timeout ?? DefaultTimeout}: {description ?? "unnamed condition"}");
            await Task.Delay(DefaultPollInterval).ConfigureAwait(false);
        }
    }
}
