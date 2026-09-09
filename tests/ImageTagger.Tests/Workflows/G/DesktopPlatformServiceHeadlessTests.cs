using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using ImageTagger.App.Services;
using ImageTagger.Tests.TestInfrastructure;
using Xunit;

namespace ImageTagger.Tests.Workflows.G;

public sealed class DesktopPlatformServiceHeadlessTests
{
    [AvaloniaFact]
    public async Task Clipboard_uses_the_Avalonia_platform_service()
    {
        var window = new Window();
        window.Show();
        var service = new DesktopPlatformService(window);

        await service.SetClipboardTextAsync("clipboard value", TestContext.Current.CancellationToken);

        Assert.Equal("clipboard value", await window.Clipboard!.TryGetTextAsync());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Missing_files_fail_without_launching_an_external_program()
    {
        using var temp = new TempDirectory("platform-missing");
        var missing = Path.Combine(temp.FullPath, "missing.png");
        var window = new Window();
        var service = new DesktopPlatformService(window);

        Assert.False(await service.OpenFileAsync(missing, TestContext.Current.CancellationToken));
        Assert.False(await service.RevealInFileManagerAsync(missing, TestContext.Current.CancellationToken));
    }
}
