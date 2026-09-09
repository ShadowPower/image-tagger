using System.Buffers;
using ImageTagger.Core;
using ImageTagger.Core.Pipelines;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageTagger.Infrastructure.Preprocessing;

/// <summary>Exact WD v1 plan that writes the final CHW float tensor directly into a batch slice.</summary>
public sealed class WdPreprocessingExecutor : IPreprocessingExecutor
{
    public const long MaximumDecodedPixels = long.MaxValue;

    public static IReadOnlyList<string> ExpectedPipelineOps { get; } =
    [
        "decode", "exif-transpose", "ensure-color", "alpha-composite", "pad-to-square",
        "resize", "reorder-channels", "cast", "divide", "normalize", "permute",
    ];

    private readonly ArrayPool<byte> _bytePool;

    public WdPreprocessingExecutor(ArrayPool<byte>? bytePool = null) =>
        _bytePool = bytePool ?? ArrayPool<byte>.Shared;

    public async ValueTask PreprocessIntoAsync(
        CompiledPreprocessingPipeline pipeline,
        IImageSource source,
        Memory<float> destination,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(source);
        ValidatePlan(pipeline, destination.Length);
        cancellationToken.ThrowIfCancellationRequested();

        using var image = await LoadValidatedImageAsync(source.Path, source.AllowLargeImage, cancellationToken).ConfigureAwait(false);
        image.Mutate(context => context.AutoOrient());
        cancellationToken.ThrowIfCancellationRequested();

        var width = image.Width;
        var height = image.Height;
        var rgbLength = checked(width * height * 3);
        var side = Math.Max(width, height);
        var paddedLength = checked(side * side * 3);
        var size = pipeline.PerSampleTensorContract.Shape[1];
        var resizedLength = checked(size * size * 3);
        byte[]? rgbBuffer = null;
        byte[]? paddedBuffer = null;
        byte[]? resizedBuffer = null;
        try
        {
            rgbBuffer = _bytePool.Rent(rgbLength);
            var rgb = rgbBuffer.AsSpan(0, rgbLength);
            if (PillowReferencePreprocessor.HasAlpha(image))
            {
                using var rgba = image.CloneAs<Rgba32>();
                PillowReferencePreprocessor.CompositeOverWhite(rgba, rgbBuffer.AsMemory(0, rgbLength));
            }
            else
            {
                using var rgbImage = image.CloneAs<Rgb24>();
                PillowReferencePreprocessor.CopyRgb(rgbImage, rgb);
            }

            cancellationToken.ThrowIfCancellationRequested();
            paddedBuffer = _bytePool.Rent(paddedLength);
            var padded = paddedBuffer.AsSpan(0, paddedLength);
            PillowReferencePreprocessor.PadToSquare(rgb, width, height, padded, side);

            cancellationToken.ThrowIfCancellationRequested();
            resizedBuffer = _bytePool.Rent(resizedLength);
            var resized = resizedBuffer.AsSpan(0, resizedLength);
            PillowBicubicResampler.ResizeRgb(padded, side, side, resized, size, size);

            cancellationToken.ThrowIfCancellationRequested();
            PillowReferencePreprocessor.ToTensor(resized, size, destination.Span);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            if (resizedBuffer is not null)
                _bytePool.Return(resizedBuffer);
            if (paddedBuffer is not null)
                _bytePool.Return(paddedBuffer);
            if (rgbBuffer is not null)
                _bytePool.Return(rgbBuffer);
        }
    }

    private static async Task<Image> LoadValidatedImageAsync(string path, bool allowLargeImage, CancellationToken cancellationToken)
    {
        try
        {
            var imageInfo = await Image.IdentifyAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new TaggerException(TaggerErrorCode.ImageCorrupt, "无法读取图片尺寸。");
            return await Image.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (UnknownImageFormatException exception)
        {
            throw new TaggerException(TaggerErrorCode.ImageUnsupported, "不支持该图片格式。", exception);
        }
        catch (InvalidImageContentException exception)
        {
            throw new TaggerException(TaggerErrorCode.ImageCorrupt, "图片内容损坏。", exception);
        }
        catch (IOException exception)
        {
            throw new TaggerException(TaggerErrorCode.IoError, "无法读取图片文件。", exception);
        }
    }

    private static void ValidatePlan(CompiledPreprocessingPipeline pipeline, int destinationLength)
    {
        if (!pipeline.Steps.Select(step => step.Op).SequenceEqual(ExpectedPipelineOps, StringComparer.Ordinal))
            throw new TaggerException(TaggerErrorCode.ModelPackInvalid, "执行器只接受已验证的 WD v1 精确流水线。");
        var contract = pipeline.PerSampleTensorContract;
        if (contract.DType != TensorDType.Float32
            || contract.Layout != "NCHW"
            || contract.Shape is not [3, var height, var width]
            || height != width
            || destinationLength != contract.ElementCount)
        {
            throw new TaggerException(TaggerErrorCode.ModelPackInvalid, "目标 batch slice 与 WD Tensor 契约不匹配。");
        }
    }
}
