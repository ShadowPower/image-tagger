namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// 通用"等比缩放"几何：在目标框内保持长宽比计算输出尺寸（不拉伸，不裁剪）。
/// 目标尺寸与源尺寸无关，源尺寸未知（decode 之后）时不产生静态结果。
/// rounding 语义：
/// <list type="bullet">
/// <item>floor：截断（等价 Python int() 与常见参考实现），默认与 torchvision 手写 fit 一致；</item>
/// <item>round：四舍五入，.5 远离零（等价 Python round() 的正数语义）。</item>
/// </list>
/// 结果钳制到 [1, 目标框]，避免极端长宽比产生 0 维。
/// </summary>
public static class ResizeFitGeometry
{
    public static (int Width, int Height) Fit(int width, int height, int boxWidth, int boxHeight, string rounding)
    {
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (boxWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(boxWidth));
        if (boxHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(boxHeight));

        double scale = Math.Min((double)boxWidth / width, (double)boxHeight / height);
        int fittedWidth = Math.Clamp(Convert(width * scale, rounding), 1, boxWidth);
        int fittedHeight = Math.Clamp(Convert(height * scale, rounding), 1, boxHeight);
        return (fittedWidth, fittedHeight);
    }

    private static int Convert(double value, string rounding) =>
        rounding == "round"
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : (int)value;
}
