namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// Bit-exact port of Pillow 12.3 Resample.c (BICUBIC, 8bpc RGB) — fixed-point
/// INT32 weights at 2^22, horizontal pass then vertical pass, saturation via
/// the same clip8 semantics as the reference lookup table. The shared engine
/// lives in <see cref="PillowFixedPointResampler"/>; this type keeps the
/// sampler identifier used by Model Packs (`pillow-bicubic-v1`).
/// </summary>
public static class PillowBicubicResampler
{
    public static void ResizeRgb(ReadOnlySpan<byte> source, int srcWidth, int srcHeight,
        Span<byte> destination, int dstWidth, int dstHeight) =>
        PillowFixedPointResampler.ResizeRgb(
            source, srcWidth, srcHeight, destination, dstWidth, dstHeight, PillowResampleFilter.Bicubic);
}
