using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ImageTagger.App.ViewModels;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageTagger.App.Controls;

/// <summary>轻量当前图确认预览：加载有限尺寸的高质量位图，不提供查看器操作。</summary>
public sealed class PreviewView : Control
{
    public static readonly StyledProperty<ImageListItemViewModel?> ItemProperty =
        AvaloniaProperty.Register<PreviewView, ImageListItemViewModel?>(nameof(Item));

    private CancellationTokenSource? _loadCancellation;
    private WriteableBitmap? _bitmap;

    static PreviewView()
    {
        AffectsRender<PreviewView>(ItemProperty);
        ItemProperty.Changed.AddClassHandler<PreviewView>((view, args) => view.StartLoad(args.NewValue as ImageListItemViewModel));
    }

    public ImageListItemViewModel? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var scale = Math.Min(Bounds.Width / _bitmap.PixelSize.Width, Bounds.Height / _bitmap.PixelSize.Height);
        var size = new Avalonia.Size(_bitmap.PixelSize.Width * scale, _bitmap.PixelSize.Height * scale);
        var origin = new Avalonia.Point((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2);
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), new Rect(origin, size));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        StartLoad(Item);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void StartLoad(ImageListItemViewModel? item)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = null;
        _bitmap?.Dispose();
        _bitmap = null;
        InvalidateVisual();
        if (item is null || VisualRoot is null)
            return;
        var cts = new CancellationTokenSource();
        _loadCancellation = cts;
        _ = LoadAsync(item, cts);
    }

    private async Task LoadAsync(ImageListItemViewModel item, CancellationTokenSource cts)
    {
        try
        {
            using var image = await SixLabors.ImageSharp.Image.LoadAsync<Bgra32>(item.CanonicalPath, cts.Token).ConfigureAwait(true);
            const int maximumEdge = 1024;
            if (image.Width > maximumEdge || image.Height > maximumEdge)
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new SixLabors.ImageSharp.Size(maximumEdge, maximumEdge),
                    Sampler = KnownResamplers.Lanczos3,
                }));
            cts.Token.ThrowIfCancellationRequested();
            var pixels = new byte[checked(image.Width * image.Height * 4)];
            image.CopyPixelDataTo(pixels);
            var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var framebuffer = bitmap.Lock())
            {
                var stride = image.Width * 4;
                for (var row = 0; row < image.Height; row++)
                    Marshal.Copy(pixels, row * stride, IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes), stride);
            }
            if (ReferenceEquals(_loadCancellation, cts) && !cts.IsCancellationRequested)
            {
                _bitmap = bitmap;
                InvalidateVisual();
            }
            else
                bitmap.Dispose();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception) { }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cts))
                _loadCancellation = null;
            cts.Dispose();
        }
    }
}
