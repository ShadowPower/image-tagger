using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageTagger.Infrastructure.Preprocessing;

public sealed record PreprocessStages(
    byte[] EnsureRgb,
    int Width,
    int Height,
    byte[] Padded,
    int Side,
    byte[] Resized,
    int Size,
    float[] Tensor);

/// <summary>
/// Executes the WD preprocessing contract with ImageSharp, mirroring the Python
/// reference (scripts/model_utils.py) stage by stage. Alpha composite over white
/// uses the exact integer formula verified against Pillow in P-05.
/// </summary>
public static class PillowReferencePreprocessor
{
    public static PreprocessStages Run(string path, int size = 448)
    {
        using var image = Image.Load(path);

        // Pillow: ImageOps.exif_transpose
        image.Mutate(ctx => ctx.AutoOrient());

        // Pillow: "transparency" in image.info -> RGBA else RGB. ImageSharp's decoded
        // pixel type carries the alpha channel; AlphaRepresentation stays null here.
        bool hasAlpha = HasAlpha(image);

        byte[] ensureRgb;
        int width, height;
        if (hasAlpha)
        {
            using var rgba = image.CloneAs<Rgba32>();
            width = rgba.Width;
            height = rgba.Height;
            ensureRgb = new byte[width * height * 3];
            CompositeOverWhite(rgba, ensureRgb);
        }
        else
        {
            using var rgb = image.CloneAs<Rgb24>();
            width = rgb.Width;
            height = rgb.Height;
            ensureRgb = new byte[width * height * 3];
            CopyRgb(rgb, ensureRgb);
        }

        int side = Math.Max(width, height);
        byte[] padded = new byte[side * side * 3];
        PadToSquare(ensureRgb, width, height, padded, side);

        byte[] resized = new byte[size * size * 3];
        PillowBicubicResampler.ResizeRgb(padded, side, side, resized, size, size);

        float[] tensor = new float[3 * size * size];
        ToTensor(resized, size, tensor);
        return new PreprocessStages(ensureRgb, width, height, padded, side, resized, size, tensor);
    }

    internal static bool HasAlpha(Image image) =>
        image is Image<Rgba32> or Image<Rgba64> or Image<La16> or Image<La32>
            or Image<Bgra32> or Image<RgbaVector>;

    internal static void CopyRgb(Image<Rgb24> image, Span<byte> output)
    {
        if (output.Length != image.Width * image.Height * 3)
            throw new ArgumentException("RGB destination length mismatch.", nameof(output));
        image.CopyPixelDataTo(output);
    }

    internal static void CompositeOverWhite(Image<Rgba32> image, Memory<byte> output)
    {
        int count = image.Width * image.Height;
        if (output.Length != count * 3)
            throw new ArgumentException("RGB destination length mismatch.", nameof(output));
        image.ProcessPixelRows(rows =>
        {
            var destination = output.Span;
            for (int y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                int offset = y * image.Width * 3;
                for (int x = 0; x < row.Length; x++)
                {
                    // (s*a + 255*(255-a) + 127) / 255 with integer division; exact vs Pillow.
                    int a = row[x].A;
                    int inv = 255 - a;
                    destination[offset] = (byte)((row[x].R * a + 255 * inv + 127) / 255);
                    destination[offset + 1] = (byte)((row[x].G * a + 255 * inv + 127) / 255);
                    destination[offset + 2] = (byte)((row[x].B * a + 255 * inv + 127) / 255);
                    offset += 3;
                }
            }
        });
    }

    internal static void PadToSquare(
        ReadOnlySpan<byte> rgb,
        int width,
        int height,
        Span<byte> padded,
        int side)
    {
        if (rgb.Length != width * height * 3 || padded.Length != side * side * 3)
            throw new ArgumentException("Pad buffer length mismatch.");
        for (int i = 0; i < padded.Length; i += 3)
            padded[i] = padded[i + 1] = padded[i + 2] = 255;

        int offsetX = (side - width) / 2;
        int offsetY = (side - height) / 2;
        for (int y = 0; y < height; y++)
        {
            int src = y * width * 3;
            int dst = ((y + offsetY) * side + offsetX) * 3;
            rgb.Slice(src, width * 3).CopyTo(padded.Slice(dst, width * 3));
        }
    }

    internal static void ToTensor(ReadOnlySpan<byte> resized, int size, Span<float> tensor)
    {
        // HWC RGB uint8 -> CHW BGR float32, x/255 then (x-0.5)/0.5.
        if (resized.Length != size * size * 3 || tensor.Length != 3 * size * size)
            throw new ArgumentException("Tensor buffer length mismatch.");
        int plane = size * size;
        for (int y = 0; y < size; y++)
        {
            int rowOffset = y * size * 3;
            for (int x = 0; x < size; x++)
            {
                int i = rowOffset + x * 3;
                tensor[0 * plane + y * size + x] = (resized[i + 2] / 255f - 0.5f) / 0.5f;
                tensor[1 * plane + y * size + x] = (resized[i + 1] / 255f - 0.5f) / 0.5f;
                tensor[2 * plane + y * size + x] = (resized[i] / 255f - 0.5f) / 0.5f;
            }
        }
    }
}
