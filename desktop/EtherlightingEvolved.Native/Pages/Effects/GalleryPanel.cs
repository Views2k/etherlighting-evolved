using Avalonia;
using Avalonia.Controls;

namespace EtherlightingEvolved.Native.Pages.Effects;

// CSS auto-fill with equal columns and a height determined by the tallest card in each row.
internal sealed class GalleryPanel : Panel
{
    bool compact;

    internal bool Compact
    {
        get => compact;
        set { if (compact != value) { compact = value; InvalidateMeasure(); } }
    }

    double Gap => compact ? 10 : 14;
    double MinimumCardWidth => compact ? 210 : 235;
    int Columns(double width) => Math.Max(1, (int)Math.Floor((width + Gap) / (MinimumCardWidth + Gap)));

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : MinimumCardWidth;
        var columns = Columns(width);
        var cardWidth = Math.Max(0, (width - (columns - 1) * Gap) / columns);
        double height = 0, rowHeight = 0;
        for (int i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            if ((i + 1) % columns == 0 || i == Children.Count - 1)
            {
                height += rowHeight;
                if (i < Children.Count - 1) height += Gap;
                rowHeight = 0;
            }
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var columns = Columns(final.Width);
        var cardWidth = Math.Max(0, (final.Width - (columns - 1) * Gap) / columns);
        double y = 0;
        for (int i = 0; i < Children.Count; i += columns)
        {
            var end = Math.Min(i + columns, Children.Count);
            double height = 0;
            for (int j = i; j < end; j++) height = Math.Max(height, Children[j].DesiredSize.Height);
            for (int j = i; j < end; j++) Children[j].Arrange(new Rect((j - i) * (cardWidth + Gap), y, cardWidth, height));
            y += height + Gap;
        }
        return final;
    }
}
