using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using ImageTagger.App.ViewModels;

namespace ImageTagger.App.Controls;

/// <summary>
/// Virtualization-friendly thumbnail surface. Loading starts only after a list
/// container is attached; transparent pixels are drawn over a checkerboard.
/// </summary>
public sealed class ThumbnailView : Control
{
    public static readonly StyledProperty<ImageListItemViewModel?> ItemProperty =
        AvaloniaProperty.Register<ThumbnailView, ImageListItemViewModel?>(nameof(Item));

    private static readonly IBrush CheckerLight = new SolidColorBrush(Color.Parse("#D9DDE3"));
    private static readonly IBrush CheckerDark = new SolidColorBrush(Color.Parse("#B8BEC7"));
    private WriteableBitmap? _bitmap;

    static ThumbnailView()
    {
        AffectsRender<ThumbnailView>(ItemProperty);
        ItemProperty.Changed.AddClassHandler<ThumbnailView>((view, args) =>
            view.ChangeItem(args.OldValue as ImageListItemViewModel, args.NewValue as ImageListItemViewModel));
    }

    public ImageListItemViewModel? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        const double cellSize = 8;
        for (double y = 0; y < Bounds.Height; y += cellSize)
        {
            for (double x = 0; x < Bounds.Width; x += cellSize)
            {
                var alternating = ((int)(x / cellSize) + (int)(y / cellSize)) % 2 == 0;
                context.FillRectangle(
                    alternating ? CheckerLight : CheckerDark,
                    new Rect(x, y, Math.Min(cellSize, Bounds.Width - x), Math.Min(cellSize, Bounds.Height - y)));
            }
        }

        if (_bitmap is null)
            return;

        var scale = Math.Min(Bounds.Width / _bitmap.PixelSize.Width, Bounds.Height / _bitmap.PixelSize.Height);
        var width = _bitmap.PixelSize.Width * scale;
        var height = _bitmap.PixelSize.Height * scale;
        var destination = new Rect((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
        context.DrawImage(_bitmap, new Rect(_bitmap.Size), destination);
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Item is not null)
        {
            Item.PropertyChanged -= OnItemPropertyChanged;
            Item.PropertyChanged += OnItemPropertyChanged;
            UpdateBitmap();
            await Item.EnsureThumbnailAsync();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Item is not null)
        {
            Item.PropertyChanged -= OnItemPropertyChanged;
            Item.CancelThumbnailRequest();
        }
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void ChangeItem(ImageListItemViewModel? oldItem, ImageListItemViewModel? newItem)
    {
        if (oldItem is not null)
            oldItem.PropertyChanged -= OnItemPropertyChanged;
        oldItem?.CancelThumbnailRequest();
        if (newItem is not null && VisualRoot is not null)
            newItem.PropertyChanged += OnItemPropertyChanged;
        UpdateBitmap();

        if (VisualRoot is not null && newItem is not null)
            _ = newItem.EnsureThumbnailAsync();
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ImageListItemViewModel.Thumbnail))
            UpdateBitmap();
    }

    private void UpdateBitmap()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        var thumbnail = Item?.Thumbnail;
        if (thumbnail is null)
        {
            InvalidateVisual();
            return;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(thumbnail.Width, thumbnail.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);
        using (var framebuffer = bitmap.Lock())
        {
            var sourceStride = thumbnail.Width * 4;
            for (var row = 0; row < thumbnail.Height; row++)
            {
                Marshal.Copy(
                    thumbnail.PixelsBgra,
                    row * sourceStride,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                    sourceStride);
            }
        }

        _bitmap = bitmap;
        InvalidateVisual();
    }
}
