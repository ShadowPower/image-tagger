using ImageTagger.Infrastructure.Preprocessing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace ImageTagger.Tests.Workflows.B;

public sealed class ImageSharpImageOperationsTests
{
    [Fact]
    public void Center_crop_and_bicubic_resize_delegate_geometry_to_ImageSharp()
    {
        using var source = new Image<Rgb24>(5, 3);
        source.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgb24((byte)(x + y * 10), 20, 30);
            }
        });

        using var cropped = ImageSharpImageOperations.CenterCrop(source, 3, 1);
        using var resized = ImageSharpImageOperations.ResizeBicubic(cropped, 6, 2);

        Assert.Equal(new Rgb24(11, 20, 30), cropped[0, 0]);
        Assert.Equal(3, cropped.Width);
        Assert.Equal(1, cropped.Height);
        Assert.Equal(6, resized.Width);
        Assert.Equal(2, resized.Height);
    }

    [Fact]
    public void Color_conversion_and_alpha_composite_delegate_pixel_handling_to_ImageSharp()
    {
        using var source = new Image<Rgba32>(2, 1);
        source[0, 0] = new Rgba32(200, 100, 50, 255);
        source[1, 0] = new Rgba32(200, 100, 50, 0);

        using var rgb = ImageSharpImageOperations.EnsureRgb(source);
        using var composited = ImageSharpImageOperations.CompositeOver(source, new Rgb24(10, 20, 30));

        Assert.Equal(new Rgb24(200, 100, 50), rgb[0, 0]);
        Assert.Equal(new Rgb24(200, 100, 50), composited[0, 0]);
        Assert.Equal(new Rgb24(10, 20, 30), composited[1, 0]);
    }
}
