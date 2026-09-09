using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(ImageTagger.Tests.TestInfrastructure.HeadlessAppBuilder))]

namespace ImageTagger.Tests.TestInfrastructure;

public static class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessTestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
                ShouldRenderOnUIThread = true,
            });
}
