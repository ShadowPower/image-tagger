using ImageTagger.Core.Domain;
using ImageTagger.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageTagger.Infrastructure.Imaging;

/// <summary>
/// Lazily decodes first-frame, EXIF-oriented thumbnails. Cached values contain
/// only small BGRA pixel arrays and never retain ImageSharp image objects.
/// Alpha is preserved so the Avalonia list can render its checkerboard beneath.
/// </summary>
public sealed class ThumbnailService : IThumbnailService, IDisposable
{
    public const int DefaultMaximumEdge = 128;
    public const int DefaultCacheCapacity = 256;

    private readonly MemoryCache _cache;
    private readonly int _maximumEdge;

    public ThumbnailService(
        int maximumEdge = DefaultMaximumEdge,
        int cacheCapacity = DefaultCacheCapacity)
    {
        if (maximumEdge <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumEdge));
        if (cacheCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(cacheCapacity));

        _maximumEdge = maximumEdge;
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = cacheCapacity });
    }

    public async Task<ThumbnailResult?> GetOrCreateAsync(
        ImageDocument image,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var key = CreateCacheKey(image);
        if (_cache.TryGetValue(key, out ThumbnailResult? cached))
            return cached;

        try
        {
            var options = new DecoderOptions
            {
                MaxFrames = 1,
                SkipMetadata = false,
                // Decoder TargetSize is allowed to upscale, so only request it
                // when the imported header proves downscaling is necessary.
                TargetSize = Math.Max(image.PixelWidth, image.PixelHeight) > _maximumEdge
                    ? new Size(_maximumEdge, _maximumEdge)
                    : null,
            };
            using var decoded = await Image.LoadAsync<Bgra32>(
                options, image.CanonicalPath, cancellationToken).ConfigureAwait(false);
            decoded.Mutate(context =>
            {
                context.AutoOrient();
                if (decoded.Width > _maximumEdge || decoded.Height > _maximumEdge)
                {
                    context.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(_maximumEdge, _maximumEdge),
                    });
                }
            });

            cancellationToken.ThrowIfCancellationRequested();
            var pixels = new byte[checked(decoded.Width * decoded.Height * 4)];
            decoded.CopyPixelDataTo(pixels);
            var result = new ThumbnailResult(decoded.Width, decoded.Height, pixels);
            _cache.Set(key, result, new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = TimeSpan.FromMinutes(10),
            });
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or UnknownImageFormatException
            or InvalidImageContentException)
        {
            return null;
        }
    }

    public void Dispose() => _cache.Dispose();

    /// <summary>Clears decoded thumbnails without disposing the service.</summary>
    public void ClearCache() => _cache.Clear();

    private static string CreateCacheKey(ImageDocument image)
    {
        try
        {
            var file = new FileInfo(image.CanonicalPath);
            return $"{image.CanonicalPath}\0{file.Length}\0{file.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"{image.CanonicalPath}\0{image.FileSize}";
        }
    }
}
