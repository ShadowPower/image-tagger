using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using ImageTagger.Core.Services;

namespace ImageTagger.App.Services;

/// <summary>Avalonia-backed dialogs, clipboard and OS desktop launching.</summary>
public sealed class DesktopPlatformService(TopLevel owner) : IPlatformService
{
    private static readonly FilePickerFileType SupportedImages = new("支持的图片")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif", "*.tif", "*.tiff"],
        MimeTypes = ["image/jpeg", "image/png", "image/webp", "image/bmp", "image/gif", "image/tiff"],
    };

    private readonly TopLevel _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<IReadOnlyList<string>?> PickImageFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图片",
            AllowMultiple = true,
            FileTypeFilter = [SupportedImages],
        });
        cancellationToken.ThrowIfCancellationRequested();
        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        return paths.Length == 0 ? null : paths;
    }

    public async Task<string?> PickFolderAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var folders = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "打开图片文件夹",
            AllowMultiple = false,
        });
        cancellationToken.ThrowIfCancellationRequested();
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string suggestedFileName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedFileName);
        cancellationToken.ThrowIfCancellationRequested();
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出 Prompt",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "txt",
            FileTypeChoices =
            [
                new FilePickerFileType("文本文件")
                {
                    Patterns = ["*.txt"],
                    MimeTypes = ["text/plain"],
                },
            ],
        });
        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }

    public Task SetClipboardTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = _owner.Clipboard
            ?? throw new InvalidOperationException("当前平台不提供剪贴板服务。");
        return clipboard.SetTextAsync(text);
    }

    public async Task<bool> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return false;
        try
        {
            var launched = await _owner.Launcher.LaunchFileInfoAsync(new FileInfo(path));
            cancellationToken.ThrowIfCancellationRequested();
            return launched;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public Task<bool> RevealInFileManagerAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
            return Task.FromResult(false);

        ProcessStartInfo? startInfo = null;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(path);
        }

        if (startInfo is null)
            return Task.FromResult(false);

        try
        {
            using var process = Process.Start(startInfo);
            return Task.FromResult(process is not null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Task.FromResult(false);
        }
    }
}
