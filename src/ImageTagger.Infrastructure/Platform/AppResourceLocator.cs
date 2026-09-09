using ImageTagger.Core.Services;

namespace ImageTagger.Infrastructure.Platform;

/// <summary>
/// Locates read-only built-in assets next to the executable and writable app
/// data under the OS user-data root (design 11.1):
/// Windows — <c>%LOCALAPPDATA%\ImageTagger</c> (via
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>);
/// macOS — <c>~/Library/Application Support/ImageTagger</c>.
/// Model locations never come from environment variables; callers store
/// Model Pack IDs only, never absolute paths.
/// </summary>
public sealed class AppResourceLocator : IAppResourceLocator
{
    private const string AppFolderName = "ImageTagger";

    public AppResourceLocator()
    {
        string userDataRoot = OperatingSystem.IsMacOS()
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var appRoot = Path.Combine(userDataRoot, AppFolderName);
        SettingsRoot = Path.Combine(appRoot, "settings");
        LogsRoot = Path.Combine(appRoot, "logs");
        CacheRoot = Path.Combine(appRoot, "cache");
    }

    public string SettingsRoot { get; }

    public string LogsRoot { get; }

    public string CacheRoot { get; }
}
