namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// Bit-exact port of Pillow 12.3 Resample.c (BICUBIC, 8bpc RGB) — fixed-point
/// INT32 weights at 2^22, horizontal pass then vertical pass, saturation via
/// the same clip8 semantics as the reference lookup table.
/// </summary>
public static class PillowBicubicResampler
{
    private const int PrecisionBits = 32 - 8 - 2; // 22

    private const double FilterSupport = 2.0;

    public static void ResizeRgb(ReadOnlySpan<byte> source, int srcWidth, int srcHeight,
        Span<byte> destination, int dstWidth, int dstHeight)
    {
        var (boundsH, kkH, ksizeH) = PrecomputeCoeffs(srcWidth, dstWidth);
        var (boundsV, kkV, ksizeV) = PrecomputeCoeffs(srcHeight, dstHeight);

        // First/last source row used by any output row; horizontal pass covers only those.
        int yBoxFirst = boundsV[0];
        int yBoxLast = boundsV[(dstHeight - 1) * 2] + boundsV[(dstHeight - 1) * 2 + 1];
        int tempHeight = yBoxLast - yBoxFirst;
        int tempLength = checked(tempHeight * dstWidth * 3);
        var temp = System.Buffers.ArrayPool<byte>.Shared.Rent(tempLength);
        try
        {
            var tempSpan = temp.AsSpan(0, tempLength);
            ResampleHorizontal(source, srcWidth, tempSpan, dstWidth, yBoxFirst, ksizeH, boundsH, kkH);
            ResampleVertical(tempSpan, dstWidth, destination, dstWidth, dstHeight, yBoxFirst, ksizeV, boundsV, kkV);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(temp);
        }
    }

    private static double BicubicFilter(double x)
    {
        const double a = -0.5;
        if (x < 0.0) x = -x;
        if (x < 1.0) return ((a + 2.0) * x - (a + 3.0)) * x * x + 1;
        if (x < 2.0) return (((x - 5) * x + 8) * x - 4) * a;
        return 0.0;
    }

    private static (int[] Bounds, int[] Kk, int Ksize) PrecomputeCoeffs(int inSize, int outSize)
    {
        double scale = (double)inSize / outSize;
        double filterscale = scale < 1.0 ? 1.0 : scale;
        double support = FilterSupport * filterscale;
        int ksize = (int)Math.Ceiling(support) * 2 + 1;

        var bounds = new int[outSize * 2];
        var kk = new int[outSize * ksize];
        double invFilterscale = 1.0 / filterscale;

        for (int xx = 0; xx < outSize; xx++)
        {
            double center = (xx + 0.5) * scale;
            double ww = 0.0;
            int xmin = (int)(center - support + 0.5);
            if (xmin < 0) xmin = 0;
            int xmax = (int)(center + support + 0.5);
            if (xmax > inSize) xmax = inSize;
            xmax -= xmin;

            var row = new double[ksize];
            for (int x = 0; x < xmax; x++)
            {
                double w = BicubicFilter((x + xmin - center + 0.5) * invFilterscale);
                row[x] = w;
                ww += w;
            }
            if (ww != 0.0)
                for (int x = 0; x < xmax; x++)
                    row[x] /= ww;

            // normalize_coeffs_8bpc: float weights -> INT32 fixed point.
            int baseIndex = xx * ksize;
            for (int x = 0; x < ksize; x++)
                kk[baseIndex + x] = (int)(row[x] < 0
                    ? -0.5 + row[x] * (1 << PrecisionBits)
                    : 0.5 + row[x] * (1 << PrecisionBits));

            bounds[xx * 2 + 0] = xmin;
            bounds[xx * 2 + 1] = xmax;
        }
        return (bounds, kk, ksize);
    }

    private static void ResampleHorizontal(ReadOnlySpan<byte> source, int srcWidth,
        Span<byte> destination, int dstWidth, int sourceRowOffset,
        int ksize, int[] bounds, int[] kk)
    {
        int rows = destination.Length / (dstWidth * 3);
        for (int yy = 0; yy < rows; yy++)
        {
            int inRow = (yy + sourceRowOffset) * srcWidth * 3;
            int outRow = yy * dstWidth * 3;
            for (int xx = 0; xx < dstWidth; xx++)
            {
                int xmin = bounds[xx * 2 + 0];
                int xmax = bounds[xx * 2 + 1];
                int k = xx * ksize;
                int ss0 = 1 << (PrecisionBits - 1);
                int ss1 = ss0, ss2 = ss0;
                for (int x = 0; x < xmax; x++)
                {
                    int inIndex = inRow + (x + xmin) * 3;
                    int weight = kk[k + x];
                    ss0 += source[inIndex] * weight;
                    ss1 += source[inIndex + 1] * weight;
                    ss2 += source[inIndex + 2] * weight;
                }
                int outIndex = outRow + xx * 3;
                destination[outIndex] = Clip8(ss0);
                destination[outIndex + 1] = Clip8(ss1);
                destination[outIndex + 2] = Clip8(ss2);
            }
        }
    }

    private static void ResampleVertical(ReadOnlySpan<byte> source, int width,
        Span<byte> destination, int dstWidth, int dstHeight,
        int sourceRowOffset, int ksize, int[] bounds, int[] kk)
    {
        for (int yy = 0; yy < dstHeight; yy++)
        {
            int ymin = bounds[yy * 2 + 0] - sourceRowOffset;
            int ymax = bounds[yy * 2 + 1];
            int k = yy * ksize;
            int outRow = yy * dstWidth * 3;
            for (int xx = 0; xx < dstWidth; xx++)
            {
                int ss0 = 1 << (PrecisionBits - 1);
                int ss1 = ss0, ss2 = ss0;
                for (int y = 0; y < ymax; y++)
                {
                    int inIndex = (y + ymin) * width * 3 + xx * 3;
                    int weight = kk[k + y];
                    ss0 += source[inIndex] * weight;
                    ss1 += source[inIndex + 1] * weight;
                    ss2 += source[inIndex + 2] * weight;
                }
                int outIndex = outRow + xx * 3;
                destination[outIndex] = Clip8(ss0);
                destination[outIndex + 1] = Clip8(ss1);
                destination[outIndex + 2] = Clip8(ss2);
            }
        }
    }

    /// <summary>Equivalent to Pillow's clip8 saturation lookup table.</summary>
    private static byte Clip8(int value)
    {
        int t = value >> PrecisionBits;
        return (byte)(t <= 0 ? 0 : t >= 255 ? 255 : t);
    }
}
