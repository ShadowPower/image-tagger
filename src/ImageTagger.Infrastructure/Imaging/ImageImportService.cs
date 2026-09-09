using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using SixLabors.ImageSharp;

namespace ImageTagger.Infrastructure.Imaging;

/// <summary>
/// Reads image headers without decoding complete pixel buffers. The service is
/// session-scoped so successful canonical paths remain deduplicated across calls.
/// </summary>
public sealed class ImageImportService : IImageImportService
{
    // Retained for API compatibility; large images are no longer rejected.
    public const long DefaultPixelLimit = long.MaxValue;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff",
    };

    private static readonly HashSet<string> SupportedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "JPEG", "PNG", "WEBP", "BMP", "GIF", "TIFF",
    };

    private readonly object _gate = new();
    private readonly HashSet<string> _knownPaths;
    private readonly HashSet<string> _pendingPaths;

    public ImageImportService(long pixelLimit = DefaultPixelLimit)
    {
        if (pixelLimit <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelLimit));

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _knownPaths = new HashSet<string>(comparer);
        _pendingPaths = new HashSet<string>(comparer);
        _ = pixelLimit; // retained compatibility parameter; no pixel rejection
    }

    public Task<ImageImportResult> ImportAsync(
        IReadOnlyList<string> paths, bool recursive, CancellationToken cancellationToken,
        IReadOnlySet<string>? allowLargeImages = null) {
        ArgumentNullException.ThrowIfNull(paths);
        return Task.Run(() => ImportCoreAsync(paths, recursive, cancellationToken, allowLargeImages), cancellationToken);
    }

    public void Forget(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var normalized = Path.GetFullPath(canonicalPath);
        lock (_gate)
            _knownPaths.Remove(normalized);
    }

    public void ClearKnownPaths()
    {
        lock (_gate)
            _knownPaths.Clear();
    }

    private async Task<ImageImportResult> ImportCoreAsync(
        IReadOnlyList<string> paths,
        bool recursive,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? allowLargeImages)
    {
        var images = new List<ImageDocument>();
        var failures = new List<ImageImportFailure>();
        var candidates = ExpandPaths(paths, recursive, failures, cancellationToken);
        var reservedPaths = new List<string>();
        var duplicateCount = 0;

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string canonicalPath;
                try
                {
                    canonicalPath = Path.GetFullPath(candidate);
                }
                catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    failures.Add(Failure(candidate, ImageImportFailureKind.NotFound, "文件路径无效。"));
                    continue;
                }

                lock (_gate)
                {
                    if (_knownPaths.Contains(canonicalPath) || !_pendingPaths.Add(canonicalPath))
                    {
                        duplicateCount++;
                        continue;
                    }
                }
                reservedPaths.Add(canonicalPath);

                try
                {
                    var document = await ReadDocumentAsync(canonicalPath, cancellationToken,
                        allowLargeImages?.Contains(canonicalPath) == true).ConfigureAwait(false);
                    images.Add(document);
                }
                catch (ImageImportException exception)
                {
                    failures.Add(new ImageImportFailure(
                        canonicalPath, Path.GetFileName(canonicalPath), exception.Kind,
                        exception.Message, exception.PixelCount));
                }
                catch (UnauthorizedAccessException)
                {
                    failures.Add(Failure(canonicalPath, ImageImportFailureKind.AccessDenied, "没有权限读取该文件。"));
                }
                catch (FileNotFoundException)
                {
                    failures.Add(Failure(canonicalPath, ImageImportFailureKind.NotFound, "文件不存在或已被删除。"));
                }
                catch (DirectoryNotFoundException)
                {
                    failures.Add(Failure(canonicalPath, ImageImportFailureKind.NotFound, "文件所在目录不存在。"));
                }
                catch (UnknownImageFormatException)
                {
                    failures.Add(Failure(canonicalPath, ImageImportFailureKind.UnsupportedFormat, "不是支持的图片格式。"));
                }
                catch (InvalidImageContentException)
                {
                    failures.Add(Failure(canonicalPath, ImageImportFailureKind.CorruptImage, "图片内容已损坏或不完整。"));
                }
                catch (IOException)
                {
                    failures.Add(Failure(canonicalPath, ImageImportFailureKind.IoError, "读取图片时发生文件错误。"));
                }
            }

            // Commit deduplication state only after the caller is guaranteed to
            // receive the whole successful result.
            lock (_gate)
                _knownPaths.UnionWith(images.Select(image => image.CanonicalPath));
            return new ImageImportResult(images, failures, duplicateCount);
        }
        finally
        {
            lock (_gate)
                _pendingPaths.ExceptWith(reservedPaths);
        }
    }

    private IEnumerable<string> ExpandPaths(
        IReadOnlyList<string> paths,
        bool recursive,
        List<ImageImportFailure> failures,
        CancellationToken cancellationToken)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                failures.Add(Failure(path ?? string.Empty, ImageImportFailureKind.NotFound, "文件路径为空。"));
                continue;
            }

            if (File.Exists(path))
            {
                yield return path;
                continue;
            }

            if (!Directory.Exists(path))
            {
                failures.Add(Failure(path, ImageImportFailureKind.NotFound, "文件或目录不存在。"));
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(
                    path,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = recursive,
                        IgnoreInaccessible = true,
                        AttributesToSkip = FileAttributes.ReparsePoint,
                        ReturnSpecialDirectories = false,
                    });
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(Failure(path, ImageImportFailureKind.AccessDenied, "没有权限读取该目录。"));
                continue;
            }
            catch (IOException)
            {
                failures.Add(Failure(path, ImageImportFailureKind.IoError, "枚举目录时发生文件错误。"));
                continue;
            }

            using var enumerator = files.GetEnumerator();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file;
                try
                {
                    if (!enumerator.MoveNext())
                        break;
                    file = enumerator.Current;
                }
                catch (UnauthorizedAccessException)
                {
                    failures.Add(Failure(path, ImageImportFailureKind.AccessDenied, "目录中有无法访问的内容。"));
                    break;
                }
                catch (IOException)
                {
                    failures.Add(Failure(path, ImageImportFailureKind.IoError, "枚举目录时发生文件错误。"));
                    break;
                }

                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                    yield return file;
            }
        }
    }

    private async Task<ImageDocument> ReadDocumentAsync(string path, CancellationToken cancellationToken, bool allowLargeImage)
    {
        _ = allowLargeImage;
        if (!SupportedExtensions.Contains(Path.GetExtension(path)))
            throw new ImageImportException(ImageImportFailureKind.UnsupportedFormat, "不支持该文件扩展名。");

        var info = await Image.IdentifyAsync(path, cancellationToken).ConfigureAwait(false)
            ?? throw new ImageImportException(ImageImportFailureKind.CorruptImage, "无法读取图片信息。");
        var format = info.Metadata.DecodedImageFormat?.Name
            ?? throw new ImageImportException(ImageImportFailureKind.CorruptImage, "无法识别图片格式。");
        if (!SupportedFormats.Contains(format))
            throw new ImageImportException(ImageImportFailureKind.UnsupportedFormat, "不支持该图片格式。");

        var file = new FileInfo(path);
        return new ImageDocument
        {
            Id = Guid.NewGuid().ToString("N"),
            CanonicalPath = path,
            FileName = file.Name,
            FileSize = file.Length,
            Format = NormalizeFormat(format),
            PixelWidth = info.Width,
            PixelHeight = info.Height,
            // Large images are explicitly supported; keep this flag true so the
            // preprocessing stage does not apply its legacy safety gate.
            LargeImageApproved = true,
        };
    }

    private static string NormalizeFormat(string format) => format.ToUpperInvariant() switch
    {
        "JPG" or "JPEG" => "jpeg",
        "TIF" or "TIFF" => "tiff",
        _ => format.ToLowerInvariant(),
    };

    private static ImageImportFailure Failure(string path, ImageImportFailureKind kind, string message) =>
        new(path, Path.GetFileName(path), kind, message);

    private sealed class ImageImportException(ImageImportFailureKind kind, string message, long? pixelCount = null) : Exception(message)
    {
        public ImageImportFailureKind Kind { get; } = kind;
        public long? PixelCount { get; } = pixelCount;
    }
}
