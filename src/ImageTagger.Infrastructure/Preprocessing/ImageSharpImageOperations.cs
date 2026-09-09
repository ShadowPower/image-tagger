using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// Thin wrappers around approved ImageSharp geometry/color operations used by
/// standard non-WD plans. WD's Pillow-exact resize and alpha paths remain the
/// narrowly approved exceptions documented by ADR 0002.
/// </summary>
public static class ImageSharpImageOperations
{
    public static Image<Rgb24> EnsureRgb(Image source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.CloneAs<Rgb24>();
    }

    public static Image<Rgb24> CompositeOver(Image<Rgba32> source, Rgb24 background)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = source.Clone();
        clone.Mutate(context => context.BackgroundColor(Color.FromRgb(
            background.R, background.G, background.B)));
        var result = clone.CloneAs<Rgb24>();
        clone.Dispose();
        return result;
    }

    public static Image<TPixel> CenterCrop<TPixel>(
        Image<TPixel> source,
        int width,
        int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0 || width > source.Width || height > source.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "Crop must fit inside the source image.");
        var x = (source.Width - width) / 2;
        var y = (source.Height - height) / 2;
        return source.Clone(context => context.Crop(new Rectangle(x, y, width, height)));
    }

    public static Image<TPixel> ResizeBicubic<TPixel>(
        Image<TPixel> source,
        int width,
        int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        ArgumentNullException.ThrowIfNull(source);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        return source.Clone(context => context.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Bicubic,
        }));
    }
}
