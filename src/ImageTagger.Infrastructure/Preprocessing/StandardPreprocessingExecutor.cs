using System.Buffers;
using ImageTagger.Core;
using ImageTagger.Core.Pipelines;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>
/// Executes every validated model pipeline through one declarative interpreter.
/// There is no model-specific fast path: the manifest is the sole source of
/// preprocessing behavior, so custom packs and the former WD pack share it.
/// </summary>
public sealed class StandardPreprocessingExecutor : IPreprocessingExecutor
{
    public StandardPreprocessingExecutor(WdPreprocessingExecutor? legacyFastPath = null) { }

    public async ValueTask PreprocessIntoAsync(
        CompiledPreprocessingPipeline pipeline,
        IImageSource source,
        Memory<float> destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(source);
        if (destination.Length != pipeline.PerSampleTensorContract.ElementCount)
            throw new TaggerException(TaggerErrorCode.ModelPackInvalid, "目标 Tensor 长度与流水线契约不一致。");
        cancellationToken.ThrowIfCancellationRequested();

        await ExecuteGenericAsync(pipeline, source, destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteGenericAsync(
        CompiledPreprocessingPipeline pipeline,
        IImageSource source,
        Memory<float> destination,
        CancellationToken cancellationToken)
    {
        Image? image = null;
        float[]? values = null;
        int[]? shape = null;
        try
        {
            foreach (var step in pipeline.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var p = step.Parameters;
                switch (step.Op)
                {
                    case "decode":
                        image = await Image.LoadAsync<Rgba32>(source.Path, cancellationToken).ConfigureAwait(false);
                        break;
                    case "exif-transpose":
                        RequireImage(image, step.Op).Mutate(c => c.AutoOrient());
                        break;
                    case "ensure-color":
                        image = Replace(image, EnsureColor(RequireImage(image, step.Op), p));
                        break;
                    case "alpha-composite":
                        if (image is Image<Rgba32> rgba)
                        {
                            var bg = ByteTriplet(p["background"]);
                            image = Replace(image, ImageSharpImageOperations.CompositeOver(
                                rgba, new Rgb24(bg[0], bg[1], bg[2])));
                        }
                        break;
                    case "pad-to-square":
                        image = Replace(image, Pad(RequireRgb(image, step.Op), ByteTriplet(p["background"])));
                        break;
                    case "resize":
                        image = Replace(image, Resize(RequireRgb(image, step.Op),
                            Int(p["width"]), Int(p["height"]), String(p["sampler"])));
                        break;
                    case "crop":
                        image = Replace(image, ImageSharpImageOperations.CenterCrop(
                            RequireRgb(image, step.Op), Int(p["width"]), Int(p["height"])));
                        break;
                    case "reorder-channels":
                        image = Replace(image, Reorder(RequireRgb(image, step.Op), IntArray(p["order"])));
                        break;
                    case "cast":
                        (values, shape) = ToHwc(RequireRgb(image, step.Op));
                        break;
                    case "divide":
                        RequireValues(values, step.Op);
                        var divisor = Number(p["value"]);
                        for (var i = 0; i < values!.Length; i++) values[i] /= (float)divisor;
                        break;
                    case "normalize":
                        RequireValues(values, step.Op);
                        var mean = NumberArray(p["mean"]);
                        var std = NumberArray(p["std"]);
                        for (var i = 0; i < values!.Length; i++)
                        {
                            var channel = i % 3;
                            values[i] = (values[i] - (float)mean[channel]) / (float)std[channel];
                        }
                        break;
                    case "permute":
                        RequireValues(values, step.Op);
                        (values, shape) = Permute(values!, shape!, IntArray(p["order"]));
                        break;
                    default:
                        throw new TaggerException(TaggerErrorCode.ModelPackInvalid,
                            $"未注册的预处理算子：{step.Op}");
                }
            }

            RequireValues(values, "pipeline");
            values!.AsSpan().CopyTo(destination.Span);
        }
        catch (UnknownImageFormatException exception)
        {
            throw new TaggerException(TaggerErrorCode.ImageUnsupported, "不支持该图片格式。", exception);
        }
        catch (InvalidImageContentException exception)
        {
            throw new TaggerException(TaggerErrorCode.ImageCorrupt, "图片内容损坏。", exception);
        }
        finally
        {
            image?.Dispose();
        }
    }

    private static Image EnsureColor(Image image, IReadOnlyDictionary<string, object?> p)
    {
        var mode = String(p["mode"]);
        if (mode == "rgb") return image.CloneAs<Rgb24>();
        if (image is Image<Rgba32> rgba && HasTransparency(rgba)) return rgba.Clone();
        return image.CloneAs<Rgb24>();
    }

    private static bool HasTransparency(Image<Rgba32> image)
    {
        var transparent = false;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height && !transparent; y++)
                foreach (var pixel in rows.GetRowSpan(y))
                    if (pixel.A != 255) { transparent = true; break; }
        });
        return transparent;
    }

    private static Image<Rgb24> Pad(Image<Rgb24> source, byte[] background)
    {
        var side = Math.Max(source.Width, source.Height);
        var result = new Image<Rgb24>(side, side, new Rgb24(background[0], background[1], background[2]));
        var ox = (side - source.Width) / 2;
        var oy = (side - source.Height) / 2;
        source.ProcessPixelRows(result, (src, dst) =>
        {
            for (var y = 0; y < src.Height; y++)
                src.GetRowSpan(y).CopyTo(dst.GetRowSpan(y + oy).Slice(ox, source.Width));
        });
        return result;
    }

    private static Image<Rgb24> Resize(Image<Rgb24> source, int width, int height, string sampler)
    {
        if (sampler == "pillow-bicubic-v1")
        {
            var input = new byte[source.Width * source.Height * 3];
            source.CopyPixelDataTo(input);
            var output = new byte[width * height * 3];
            PillowBicubicResampler.ResizeRgb(input, source.Width, source.Height, output, width, height);
            var result = Image.LoadPixelData<Rgb24>(output, width, height);
            return result;
        }
        return ImageSharpImageOperations.ResizeBicubic(source, width, height);
    }

    private static Image<Rgb24> Reorder(Image<Rgb24> source, int[] order)
    {
        var result = new Image<Rgb24>(source.Width, source.Height);
        source.ProcessPixelRows(result, (src, dst) =>
        {
            for (var y = 0; y < src.Height; y++)
            {
                var input = src.GetRowSpan(y);
                var output = dst.GetRowSpan(y);
                for (var x = 0; x < source.Width; x++)
                {
                    var p = input[x];
                    output[x] = new Rgb24(Channel(p, order[0]), Channel(p, order[1]), Channel(p, order[2]));
                }
            }
        });
        return result;
    }

    private static (float[] Values, int[] Shape) ToHwc(Image<Rgb24> image)
    {
        var values = new float[checked(image.Width * image.Height * 3)];
        var index = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (var p in rows.GetRowSpan(y))
                {
                    values[index++] = p.R;
                    values[index++] = p.G;
                    values[index++] = p.B;
                }
        });
        return (values, [image.Height, image.Width, 3]);
    }

    private static (float[] Values, int[] Shape) Permute(float[] input, int[] shape, int[] order)
    {
        var outputShape = order.Select(i => shape[i]).ToArray();
        var output = new float[input.Length];
        var coords = new int[shape.Length];
        for (var flat = 0; flat < input.Length; flat++)
        {
            var remainder = flat;
            for (var axis = shape.Length - 1; axis >= 0; axis--)
            {
                coords[axis] = remainder % shape[axis];
                remainder /= shape[axis];
            }
            var target = 0;
            for (var axis = 0; axis < outputShape.Length; axis++)
                target = target * outputShape[axis] + coords[order[axis]];
            output[target] = input[flat];
        }
        return (output, outputShape);
    }

    private static Image RequireImage(Image? image, string op) =>
        image ?? throw new TaggerException(TaggerErrorCode.ModelPackInvalid, $"算子 {op} 缺少图像输入。");

    private static Image<Rgb24> RequireRgb(Image? image, string op) => image as Image<Rgb24>
        ?? throw new TaggerException(TaggerErrorCode.ModelPackInvalid, $"算子 {op} 需要 RGB 图像输入。");

    private static Image? Replace(Image? old, Image next) { old?.Dispose(); return next; }
    private static byte Channel(Rgb24 pixel, int index) => index switch
    {
        0 => pixel.R,
        1 => pixel.G,
        2 => pixel.B,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };
    private static void RequireValues(float[]? values, string op) =>
        _ = values ?? throw new TaggerException(TaggerErrorCode.ModelPackInvalid, $"算子 {op} 缺少 Tensor 输入。");
    private static string String(object? value) => value as string ?? throw new ArgumentException("Expected string parameter.");
    private static int Int(object? value) => checked(Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture));
    private static double Number(object? value) => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
    private static int[] IntArray(object? value) => ((System.Collections.IEnumerable)value!).Cast<object>().Select(v => Int(v)).ToArray();
    private static double[] NumberArray(object? value) => ((System.Collections.IEnumerable)value!).Cast<object>().Select(Number).ToArray();
    private static byte[] ByteTriplet(object? value) => NumberArray(value).Select(v => checked((byte)v)).ToArray();
}
