using ImageTagger.Core.Services;
using ImageTagger.Infrastructure.Imaging;
using ImageTagger.Tests.TestInfrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ImageTagger.Tests.Workflows.A;

public sealed class ImageImportServiceTests
{
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("bmp")]
    [InlineData("gif")]
    [InlineData("tiff")]
    public async Task Imports_every_supported_container_from_header_information(string format)
    {
        using var temp = new TempDirectory($"import-{format}");
        var path = SaveImage(temp.FullPath, $"sample.{Extension(format)}", format, 7, 5);
        var expectedLength = new FileInfo(path).Length;

        var result = await new ImageImportService().ImportAsync([path], false, TestContext.Current.CancellationToken);

        var image = Assert.Single(result.Images);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.DuplicateCount);
        Assert.Equal(Path.GetFullPath(path), image.CanonicalPath);
        Assert.Equal(Path.GetFileName(path), image.FileName);
        Assert.Equal(expectedLength, image.FileSize);
        Assert.Equal(format, image.Format);
        Assert.Equal(7, image.PixelWidth);
        Assert.Equal(5, image.PixelHeight);
    }

    [Fact]
    public async Task Preserves_input_order_and_deduplicates_within_and_across_calls()
    {
        using var temp = new TempDirectory("import-order");
        var first = SaveImage(temp.FullPath, "first.png", "png", 2, 3);
        var second = SaveImage(temp.FullPath, "second.png", "png", 4, 5);
        var service = new ImageImportService();

        var initial = await service.ImportAsync(
            [second, first, second], false, TestContext.Current.CancellationToken);
        var repeated = await service.ImportAsync(
            [first], false, TestContext.Current.CancellationToken);

        Assert.Equal(["second.png", "first.png"], initial.Images.Select(x => x.FileName));
        Assert.Equal(1, initial.DuplicateCount);
        Assert.Empty(repeated.Images);
        Assert.Equal(1, repeated.DuplicateCount);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
    }

    [Fact]
    public async Task Directory_scan_filters_irrelevant_files_and_honors_recursive_setting()
    {
        using var temp = new TempDirectory("import-directory");
        SaveImage(temp.FullPath, "top.png", "png", 2, 2);
        File.WriteAllText(Path.Combine(temp.FullPath, "notes.txt"), "not an image");
        var child = temp.CreateSubdirectory("child");
        SaveImage(child, "nested.jpg", "jpeg", 2, 2);

        var shallow = await new ImageImportService().ImportAsync(
            [temp.FullPath], false, TestContext.Current.CancellationToken);
        var recursive = await new ImageImportService().ImportAsync(
            [temp.FullPath], true, TestContext.Current.CancellationToken);

        Assert.Equal("top.png", Assert.Single(shallow.Images).FileName);
        Assert.Empty(shallow.Failures);
        Assert.Equal(2, recursive.Images.Count);
        Assert.Contains(recursive.Images, x => x.FileName == "nested.jpg");
        Assert.Empty(recursive.Failures);
    }

    [Fact]
    public async Task Bad_items_are_reported_individually_without_losing_valid_images()
    {
        using var temp = new TempDirectory("import-errors");
        var valid = SaveImage(temp.FullPath, "valid.png", "png", 2, 2);
        var corrupt = temp.WriteFile("corrupt.png", [1, 2, 3, 4]);
        var unsupported = temp.WriteFile("unsupported.txt", [1, 2, 3, 4]);
        var missing = Path.Combine(temp.FullPath, "deleted.jpg");

        var result = await new ImageImportService().ImportAsync(
            [corrupt, valid, unsupported, missing], false, TestContext.Current.CancellationToken);

        Assert.Equal("valid.png", Assert.Single(result.Images).FileName);
        Assert.Equal(3, result.Failures.Count);
        Assert.Contains(result.Failures, x => x.Path == Path.GetFullPath(corrupt)
            && x.Kind is ImageImportFailureKind.CorruptImage or ImageImportFailureKind.UnsupportedFormat);
        Assert.Contains(result.Failures, x => x.Path == Path.GetFullPath(unsupported)
            && x.Kind == ImageImportFailureKind.UnsupportedFormat);
        Assert.Contains(result.Failures, x => x.Path == missing
            && x.Kind == ImageImportFailureKind.NotFound);
    }

    [Fact]
    public async Task Oversized_image_is_imported_without_pixel_limit_rejection()
    {
        using var temp = new TempDirectory("import-limit");
        var path = SaveImage(temp.FullPath, "large.png", "png", 11, 10);

        var result = await new ImageImportService(pixelLimit: 100).ImportAsync(
            [path], false, TestContext.Current.CancellationToken);

        Assert.Equal("large.png", Assert.Single(result.Images).FileName);
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task Cancellation_stops_the_import_operation()
    {
        using var temp = new TempDirectory("import-cancel");
        var path = SaveImage(temp.FullPath, "image.png", "png", 2, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ImageImportService().ImportAsync([path], false, cancellation.Token));
    }

    [Fact]
    public async Task Removed_or_cleared_session_paths_can_be_imported_again()
    {
        using var temp = new TempDirectory("import-forget");
        var first = SaveImage(temp.FullPath, "first.png", "png", 2, 2);
        var second = SaveImage(temp.FullPath, "second.png", "png", 2, 2);
        var service = new ImageImportService();
        Assert.Equal(2, (await service.ImportAsync(
            [first, second], false, TestContext.Current.CancellationToken)).Images.Count);

        service.Forget(first);
        var afterForget = await service.ImportAsync(
            [first, second], false, TestContext.Current.CancellationToken);
        Assert.Equal("first.png", Assert.Single(afterForget.Images).FileName);
        Assert.Equal(1, afterForget.DuplicateCount);

        service.ClearKnownPaths();
        Assert.Equal(2, (await service.ImportAsync(
            [first, second], false, TestContext.Current.CancellationToken)).Images.Count);
    }

    private static string SaveImage(
        string directory,
        string fileName,
        string format,
        int width,
        int height)
    {
        var path = Path.Combine(directory, fileName);
        using var image = new Image<Rgba32>(width, height, new Rgba32(20, 40, 60, 128));
        switch (format)
        {
            case "jpeg": image.SaveAsJpeg(path); break;
            case "png": image.SaveAsPng(path); break;
            case "webp": image.SaveAsWebp(path); break;
            case "bmp": image.SaveAsBmp(path); break;
            case "gif": image.SaveAsGif(path); break;
            case "tiff": image.SaveAsTiff(path); break;
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }

        return path;
    }

    private static string Extension(string format) => format switch
    {
        "jpeg" => "jpg",
        "tiff" => "tif",
        _ => format,
    };
}
