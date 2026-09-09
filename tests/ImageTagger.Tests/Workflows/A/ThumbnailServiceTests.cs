using ImageTagger.Core.Domain;
using ImageTagger.Infrastructure.Imaging;
using ImageTagger.Tests.TestInfrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ImageTagger.Tests.Workflows.A;

public sealed class ThumbnailServiceTests
{
    [Fact]
    public async Task Scales_to_bounded_edge_without_stretching()
    {
        using var temp = new TempDirectory("thumbnail-size");
        var path = SavePng(temp.FullPath, "wide.png", 300, 100, new Rgba32(10, 20, 30, 255));
        using var service = new ThumbnailService();

        var result = await service.GetOrCreateAsync(Document(path, 300, 100), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(128, result.Width);
        Assert.InRange(result.Height, 42, 43);
        Assert.Equal(result.Width * result.Height * 4, result.PixelsBgra.Length);
    }

    [Fact]
    public async Task Honors_exif_orientation_before_returning_pixels()
    {
        using var temp = new TempDirectory("thumbnail-orientation");
        var path = Path.Combine(temp.FullPath, "rotated.jpg");
        using (var image = new Image<Rgba32>(40, 20, new Rgba32(10, 20, 30, 255)))
        {
            image.Metadata.ExifProfile = new ExifProfile();
            image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
            image.SaveAsJpeg(path);
        }
        using var service = new ThumbnailService();

        var result = await service.GetOrCreateAsync(Document(path, 40, 20, "jpeg"), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(20, result.Width);
        Assert.Equal(40, result.Height);
    }

    [Fact]
    public async Task Preserves_transparency_as_bgra_for_checkerboard_rendering()
    {
        using var temp = new TempDirectory("thumbnail-alpha");
        var path = SavePng(temp.FullPath, "alpha.png", 1, 1, new Rgba32(10, 20, 30, 128));
        using var service = new ThumbnailService();

        var result = await service.GetOrCreateAsync(Document(path, 1, 1), TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal([30, 20, 10, 128], result.PixelsBgra);
    }

    [Fact]
    public async Task Capacity_evicts_old_pixels_and_never_retains_source_images()
    {
        using var temp = new TempDirectory("thumbnail-eviction");
        var firstPath = SavePng(temp.FullPath, "first.png", 2, 2, new Rgba32(1, 2, 3, 255));
        var secondPath = SavePng(temp.FullPath, "second.png", 2, 2, new Rgba32(4, 5, 6, 255));
        var first = Document(firstPath, 2, 2);
        using var service = new ThumbnailService(cacheCapacity: 1);

        Assert.NotNull(await service.GetOrCreateAsync(first, TestContext.Current.CancellationToken));
        Assert.NotNull(await service.GetOrCreateAsync(Document(secondPath, 2, 2), TestContext.Current.CancellationToken));
        File.Delete(firstPath);

        Assert.Null(await service.GetOrCreateAsync(first, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_or_deleted_file_returns_null_but_cancellation_propagates()
    {
        using var temp = new TempDirectory("thumbnail-errors");
        var corrupt = temp.WriteFile("bad.png", [1, 2, 3]);
        using var service = new ThumbnailService();

        Assert.Null(await service.GetOrCreateAsync(Document(corrupt, 1, 1), TestContext.Current.CancellationToken));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.GetOrCreateAsync(Document(corrupt, 1, 1), cancellation.Token));
    }

    private static string SavePng(string directory, string name, int width, int height, Rgba32 color)
    {
        var path = Path.Combine(directory, name);
        using var image = new Image<Rgba32>(width, height, color);
        image.SaveAsPng(path);
        return path;
    }

    private static ImageDocument Document(string path, int width, int height, string format = "png") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CanonicalPath = Path.GetFullPath(path),
        FileName = Path.GetFileName(path),
        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
        Format = format,
        PixelWidth = width,
        PixelHeight = height,
    };
}
