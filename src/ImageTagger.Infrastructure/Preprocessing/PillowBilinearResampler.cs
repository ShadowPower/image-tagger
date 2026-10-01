namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// Bit-exact port of Pillow 12.3 Resample.c (BILINEAR, 8bpc RGB) — triangle filter
/// with support scaled by the downscaling factor (Pillow's anti-aliased bilinear),
/// shared fixed-point engine with `pillow-bicubic-v1`. Matches the sampler expected
/// by models whose reference preprocessing resizes with an anti-aliased bilinear
/// kernel (e.g. torchvision bilinear with antialias enabled) to within one 8-bit LSB.
/// </summary>
public static class PillowBilinearResampler
{
    public static void ResizeRgb(ReadOnlySpan<byte> source, int srcWidth, int srcHeight,
        Span<byte> destination, int dstWidth, int dstHeight) =>
        PillowFixedPointResampler.ResizeRgb(
            source, srcWidth, srcHeight, destination, dstWidth, dstHeight, PillowResampleFilter.Bilinear);
}
