using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.TestInfrastructure;

/// <summary>
/// Proves the headless Avalonia environment works: windows can be created,
/// shown, layouted and dispatcher jobs pumped deterministically.
/// </summary>
public class HeadlessSmokeTests
{
    [AvaloniaFact]
    public void Window_can_be_created_shown_and_pumped()
    {
        var text = new TextBlock { Text = "headless smoke" };
        var window = new Window { Width = 400, Height = 300, Content = text };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(window.IsVisible);
        Assert.True(window.Bounds.Width > 0);
        window.Close();
    }

    [AvaloniaFact]
    public async Task PumpUntilAsync_drains_posted_jobs()
    {
        var completed = false;
        _ = Dispatcher.UIThread.InvokeAsync(() => completed = true);

        await Determinism.PumpUntilAsync(() => completed, timeout: TimeSpan.FromSeconds(5), "posted job runs");
    }

    [AvaloniaFact]
    public void Headless_application_is_available()
    {
        Assert.NotNull(Application.Current);
    }
}
