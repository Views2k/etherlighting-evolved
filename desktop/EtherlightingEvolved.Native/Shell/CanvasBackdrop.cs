using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Shell;

/// <summary>
/// The page background: the surface color under the theme's canvas tint gradients. The gradients cover the whole
/// view, so drawing them on the processor again under every port color change was most of the cost of moving
/// lighting. They are drawn once per size, display scale and theme into a bitmap, and changes copy from it.
/// </summary>
internal sealed class CanvasBackdrop : Control
{
    RenderTargetBitmap? cache;
    (Size Size, double Scaling, string Theme) drawn;

    public CanvasBackdrop()
    {
        IsHitTestVisible = false;
        AttachedToVisualTree += (_, _) => Look.ThemeChanged += OnThemeChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Look.ThemeChanged -= OnThemeChanged;
            cache?.Dispose();
            cache = null;
        };
    }

    void OnThemeChanged(string theme) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var key = (Bounds.Size, scaling, Look.CurrentTheme);
        if (cache is null || drawn != key)
        {
            cache?.Dispose();
            var pixels = new PixelSize((int)Math.Ceiling(Bounds.Width * scaling), (int)Math.Ceiling(Bounds.Height * scaling));
            // Plain pixels (96 DPI): the bitmap is filled edge to edge and scaled back when it is drawn.
            cache = new RenderTargetBitmap(pixels);
            using var surface = cache.CreateDrawingContext();
            var area = new Rect(0, 0, pixels.Width, pixels.Height);
            if (Application.Current?.FindResource("surface") is IBrush color) surface.FillRectangle(color, area);
            if (Application.Current?.FindResource("canvas-tint") is IBrush[] layers)
                foreach (var layer in layers) surface.FillRectangle(layer, area);
            drawn = key;
        }
        context.DrawImage(cache, new Rect(0, 0, cache.PixelSize.Width, cache.PixelSize.Height), new Rect(Bounds.Size));
    }
}
