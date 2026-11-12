using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering;

namespace EtherlightingEvolved.Native;

/// <summary>
/// Draws the port faceplate. It redraws only when a port's color or text changes; steady lighting costs nothing.
/// Rows hold up to ten ports, Ethernet first, then the SFP cages.
/// </summary>
public sealed class PortMap : Control, ICustomHitTest
{
    const double Gap = 12, SectionGap = 26, HeadingHeight = 26, MaxTile = 128, MinTile = 76, Padding = 12;

    sealed class Tile
    {
        public PortView View = null!;
        public Rect Bounds, Socket;
        public IBrush Led = Palette.SocketOff;
        public IBrush? Glow;
        public FormattedText? Number, Label, Link, Power;
        public double TextWidth = -1;
    }

    static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI, $Default"));
    static readonly Typeface Strong = new(new FontFamily("Segoe UI Variable Text, Segoe UI, $Default"), FontStyle.Normal, FontWeight.SemiBold);

    readonly List<Tile> tiles = [];
    HashSet<int> selected = [];
    readonly List<(FormattedText Text, Point At)> headings = [];
    double laidOutWidth = -1, height;

    public int InvalidateCount { get; private set; }

    public int RenderCount { get; private set; }

    public int TileCount => tiles.Count;

    public PortMap() => Cursor = new Cursor(StandardCursorType.Hand);

    /// <summary>A port tile was pressed, with the keyboard modifiers held at the time.</summary>
    public event Action<int, KeyModifiers>? PortPressed;

    public IReadOnlyList<int> PortOrder => tiles.Select(tile => tile.View.Port).ToList();

    public void SetSelection(IReadOnlyCollection<int> ports)
    {
        if (selected.SetEquals(ports)) return;
        selected = [.. ports];
        Invalidate();
    }

    public bool HitTest(Point point) => true;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        if (tiles.FirstOrDefault(tile => tile.Bounds.Contains(point)) is not { } hit) return;
        PortPressed?.Invoke(hit.View.Port, e.KeyModifiers);
        e.Handled = true;
    }

    /// <summary>The colored socket of a port, in this control's coordinates.</summary>
    public Rect SocketOf(int port) => tiles.FirstOrDefault(tile => tile.View.Port == port)?.Socket ?? default;

    /// <summary>Shows a new set of ports. Returns true when anything visible changed.</summary>
    public bool Update(IReadOnlyList<PortView> ports)
    {
        var structural = ports.Count != tiles.Count;
        for (var i = 0; !structural && i < ports.Count; i++)
            structural = ports[i].Port != tiles[i].View.Port || ports[i].Sfp != tiles[i].View.Sfp;
        if (structural)
        {
            tiles.Clear();
            foreach (var port in ports)
            {
                var tile = new Tile { View = port };
                Paint(tile);
                tiles.Add(tile);
            }
            laidOutWidth = -1;
            InvalidateMeasure();
            Invalidate();
            return true;
        }
        var changed = false;
        for (var i = 0; i < ports.Count; i++)
        {
            var tile = tiles[i];
            var next = ports[i];
            if (next == tile.View) continue;
            var colorChanged = next.R != tile.View.R || next.G != tile.View.G || next.B != tile.View.B;
            tile.View = next;
            if (colorChanged) Paint(tile);
            tile.TextWidth = -1;
            changed = true;
        }
        if (changed) Invalidate();
        return changed;
    }

    void Invalidate()
    {
        InvalidateCount++;
        InvalidateVisual();
    }

    static void Paint(Tile tile)
    {
        var view = tile.View;
        var lit = Math.Max(view.R, Math.Max(view.G, view.B)) > 6;
        tile.Led = lit ? new ImmutableSolidColorBrush(Color.FromRgb(view.R, view.G, view.B)) : Palette.SocketOff;
        tile.Glow = lit
            ? new RadialGradientBrush
            {
                GradientStops = new GradientStops
                {
                    new GradientStop(Color.FromArgb(105, view.R, view.G, view.B), 0),
                    new GradientStop(Color.FromArgb(0, view.R, view.G, view.B), 1),
                },
            }
            : null;
    }

    void Layout(double width)
    {
        if (Math.Abs(width - laidOutWidth) < 0.5) return;
        laidOutWidth = width;
        headings.Clear();
        var copper = tiles.Where(tile => !tile.View.Sfp).ToList();
        var sfp = tiles.Where(tile => tile.View.Sfp).ToList();
        var wanted = Math.Max(1, Math.Min(10, Math.Max(copper.Count, sfp.Count)));
        var fit = Math.Max(1, (int)Math.Floor((width + Gap) / (MinTile + Gap)));
        var columns = Math.Min(wanted, fit);
        var tileWidth = Math.Min(MaxTile, (width - Gap * (columns - 1)) / columns);
        var socketHeight = Math.Round((tileWidth - Padding * 2) * 0.58);
        var tileHeight = socketHeight + 100;
        double y = 0;
        foreach (var (section, name) in new[] { (copper, "ETHERNET"), (sfp, "SFP") })
        {
            if (section.Count == 0) continue;
            if (y > 0) y += SectionGap;
            var first = section[0].View.Port;
            var last = section[^1].View.Port;
            headings.Add((Text($"{name}   Ports {first}–{last}", Strong, 11, Palette.Muted, width), new Point(0, y)));
            y += HeadingHeight;
            for (var i = 0; i < section.Count; i++)
            {
                var tile = section[i];
                tile.Bounds = new Rect(i % columns * (tileWidth + Gap), y + i / columns * (tileHeight + Gap), tileWidth, tileHeight);
                var socketWidth = tileWidth - Padding * 2;
                var socket = tile.View.Sfp ? socketHeight * 0.72 : socketHeight;
                tile.Socket = new Rect(tile.Bounds.X + Padding, tile.Bounds.Y + 34 + (socketHeight - socket) / 2, socketWidth, socket);
                tile.TextWidth = -1;
            }
            y += (section.Count + columns - 1) / columns * (tileHeight + Gap) - Gap;
        }
        height = y;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Layout(double.IsFinite(availableSize.Width) ? availableSize.Width : 1100);
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : laidOutWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        RenderCount++;
        foreach (var (text, at) in headings) context.DrawText(text, at);
        foreach (var tile in tiles) Draw(context, tile, selected.Contains(tile.View.Port));
    }

    static void Draw(DrawingContext context, Tile tile, bool chosen)
    {
        var bounds = tile.Bounds;
        if (tile.TextWidth != bounds.Width) BuildText(tile);
        context.DrawRectangle(chosen ? Palette.TileSelected : Palette.Tile, chosen ? Palette.Selected : Palette.TileEdge, new RoundedRect(bounds, 10));
        var socket = tile.Socket;
        if (tile.Glow is { } glow)
            context.DrawEllipse(glow, null, socket.Center, socket.Width * 0.66, socket.Height * 0.95);
        context.DrawRectangle(tile.Led, Palette.SocketEdge, new RoundedRect(socket, 6));
        var well = tile.View.Sfp
            ? new Rect(socket.X + socket.Width * 0.14, socket.Y + socket.Height * 0.2, socket.Width * 0.72, socket.Height * 0.6)
            : new Rect(socket.X + socket.Width * 0.24, socket.Y + socket.Height * 0.24, socket.Width * 0.52, socket.Height * 0.56);
        context.DrawRectangle(Palette.Well, Palette.WellEdge, new RoundedRect(well, 3));
        context.DrawText(tile.Number!, new Point(bounds.X + Padding, bounds.Y + 10));
        if (tile.Power is { } power)
            context.DrawText(power, new Point(bounds.Right - Padding - power.Width, bounds.Y + 11));
        var textTop = bounds.Y + 34 + (tile.Socket.Height > 0 ? Math.Max(tile.Socket.Bottom - bounds.Y - 34, 0) : 0) + 14;
        context.DrawText(tile.Label!, new Point(bounds.X + Padding, textTop));
        context.DrawText(tile.Link!, new Point(bounds.X + Padding, textTop + 21));
    }

    static void BuildText(Tile tile)
    {
        var width = Math.Max(10, tile.Bounds.Width - Padding * 2);
        var view = tile.View;
        tile.Number = Text(view.Port.ToString(CultureInfo.InvariantCulture), Strong, 12, Palette.Muted, width);
        tile.Label = Text(view.Label, view.Uplink ? Strong : Face, 12.5, Palette.Text, width);
        tile.Link = Text(view.Link, Face, 11.5, view.Online ? Palette.Muted : Palette.Dim, width);
        tile.Power = view.Power.Length > 0 ? Text(view.Power, Face, 11, Palette.Muted, width) : null;
        tile.TextWidth = tile.Bounds.Width;
    }

    static FormattedText Text(string text, Typeface face, double size, IBrush brush, double width) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = width,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
}

static class Palette
{
    public static readonly IBrush Window = new ImmutableSolidColorBrush(Color.Parse("#15171d"));
    public static readonly IBrush Tile = new ImmutableSolidColorBrush(Color.Parse("#1e212a"));
    public static readonly IPen TileEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#2b303c")), 1);
    public static readonly IBrush SocketOff = new ImmutableSolidColorBrush(Color.Parse("#0d0f13"));
    public static readonly IPen SocketEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#3a4150")), 1);
    public static readonly IBrush Well = new ImmutableSolidColorBrush(Color.Parse("#0b0d12"));
    public static readonly IPen WellEdge = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#4a5263")), 1);
    public static readonly IBrush Text = new ImmutableSolidColorBrush(Color.Parse("#e7eaf0"));
    public static readonly IBrush Muted = new ImmutableSolidColorBrush(Color.Parse("#8d95a5"));
    public static readonly IBrush Dim = new ImmutableSolidColorBrush(Color.Parse("#5c6372"));
    public static readonly IBrush Live = new ImmutableSolidColorBrush(Color.Parse("#7fd9a6"));
    public static readonly IBrush Waiting = new ImmutableSolidColorBrush(Color.Parse("#f2c66d"));
    public static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#ff8f8f"));
    public static readonly IBrush TileSelected = new ImmutableSolidColorBrush(Color.Parse("#252b3a"));
    public static readonly IPen Selected = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#8fb4ff")), 2);
    public static readonly IBrush Panel = new ImmutableSolidColorBrush(Color.Parse("#1a1d25"));
}
