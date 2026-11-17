using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using PM = EtherlightingEvolved.Native.Core.Model.PortMap;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Lighting;

internal sealed class PortTile : Button
{
    readonly int number;
    public bool Sfp { get; }
    readonly TextBlock caption = U.Text("", "small", true), speed = U.Text("", "small muted"), power = U.Text("", "small muted");
    readonly Avalonia.Controls.Shapes.Path badge = new() { Width = 15, Height = 14, Stretch = Stretch.Uniform };
    readonly Avalonia.Controls.Shapes.Path check = new() { Width = 11, Height = 9, Stretch = Stretch.Uniform, Data = Geometry.Parse("M0 5 L3.5 8 L10 1"), StrokeThickness = 1.8 };
    readonly Border speedDot = new() { Width = 5, Height = 5, CornerRadius = new CornerRadius(3) };
    static readonly Geometry Uplink = Geometry.Parse("M1 8L8 1L15 8");
    static readonly Geometry Poe = Geometry.Parse("M12.5 2L4 13H10L9 22L17 10H11L12.5 2Z");
    readonly Socket socket;
    readonly Border preview;
    bool compact;
    public bool Compact
    {
        set
        {
            if (value == compact)
                return;
            compact = value;
            Padding = new Thickness(value || Sfp ? 1 : 4, value ? 1 : 6);
            caption.MinHeight = value ? 14 : 32;
            socket.Margin = new Thickness(0, value ? 2 : 5, 0, value ? 2 : 7);
        }
    }
    KeyModifiers modifiers;
    string? description;
    bool highlighted;
    bool? lastUplink;
    public PortTile(int id, bool sfp, Action<int, KeyModifiers> select)
    {
        number = id;
        Sfp = sfp;
        socket = new(sfp);
        Classes.Add("quiet");
        Margin = new Thickness(1);
        Padding = new Thickness(sfp ? 1 : 4, 6);
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        var heading = new Grid { ColumnDefinitions = new("*,Auto,*") };
        var num = U.Text(id.ToString(), "small");
        heading.Children.Add(num);
        heading.Children.Add(check);
        Grid.SetColumn(check, 1);
        heading.Children.Add(badge);
        Grid.SetColumn(badge, 2);
        badge.HorizontalAlignment = HorizontalAlignment.Right;
        check.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, "port-checkmark");
        foreach (var t in new[] { caption, speed, power })
            t.TextAlignment = TextAlignment.Center;
        caption.MinHeight = 34;
        caption.FontWeight = FontWeight.SemiBold;
        power.MinHeight = 16;
        socket.Margin = new Thickness(0, 5, 0, 7);
        speed.FontSize = 10;
        speed.TextWrapping = TextWrapping.Wrap;
        speed.TextTrimming = TextTrimming.None;
        var speedRow = new Grid { ColumnDefinitions = new("Auto,*"), HorizontalAlignment = HorizontalAlignment.Center, ColumnSpacing = 3 };
        speedDot.VerticalAlignment = VerticalAlignment.Center;
        speedRow.Children.Add(speedDot);
        speedRow.Children.Add(speed);
        Grid.SetColumn(speed, 1);
        var stack = U.Column(3, heading, socket, caption, speedRow, power);
        preview = new Border { Child = stack, Padding = new Thickness(2), CornerRadius = new CornerRadius(5) };
        preview.Bind(Border.BorderBrushProperty, "port-preview-outline");
        Content = preview;
        AddHandler(PointerPressedEvent, (_, e) => modifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, (_, e) => modifiers = e.KeyModifiers, RoutingStrategies.Tunnel);
        Click += (_, _) =>
        {
            select(id, modifiers);
            modifiers = KeyModifiers.None;
        };
    }
    public bool Selected
    {
        set
        {
            check.IsVisible = value;
        }
    }
    public bool Previewed
    {
        set
        {
            if (highlighted == value)
                return;
            highlighted = value;
            preview.BorderThickness = new Thickness(value ? 1 : 0);
        }
    }
    public void PaintColor(double[] rgb) => socket.Set(rgb);
    /// <summary>How many times the LED color changed and was repainted (development checks).</summary>
    internal int LedRepaints => socket.Repaints;
    public void Read(JsonObject port)
    {
        var link = PM.LinkStatus(port);
        var watts = PM.PortPower(port);
        var label = PM.PortLabel(port, Sfp);
        var signature = string.Join('|', label.Str("text"), link.Str("label"), watts.Str("watts"), PM.IsUplink(port), watts.Bool("delivering"));
        if (description == signature)
            return;
        description = signature;
        caption.Text = string.Join("\n", label.Arr("lines").Select(n => n.Str()));
        speed.Text = link.Str("label");
        power.Text = watts.Str("watts");
        var uplink = PM.IsUplink(port);
        badge.Data = uplink ? Uplink : watts.Bool("delivering") == true ? Poe : null;
        badge.Fill = uplink ? null : U.Brush("#aca9b9");
        badge.Stroke = uplink ? U.Brush("#6df3cf") : null;
        badge.StrokeThickness = 1.5;
        speedDot.Background = SpeedBrush(link["key"]?.ToString());
        if (lastUplink != uplink)
        {
            lastUplink = uplink;
            caption.Bind(TextBlock.ForegroundProperty, uplink ? "success" : "text");
        }
        var name = $"{(Sfp ? "SFP" : "Port")} {number} · {label.Str("text")} · {speed.Text}";
        AutomationProperties.SetName(this, name);
        ToolTip.SetTip(this, name);
        ToolTip.SetTip(power, watts.Str("description"));
    }
    internal static IBrush SpeedBrush(string? key) => U.Brush(key switch
    {
        "100" => "#BEE527",
        "1000" => "#3FDE6F",
        "2500" => "#9ECAFF",
        "5000" => "#3998FF",
        "10000" => "#41ADDD",
        "25000" => "#B69CFF",
        "40000" => "#9278E5",
        "100000" => "#DF8FE4",
        "offline" or "disabled" => "#3D4247",
        _ => "#8394A4",
    });
    sealed class Socket : Panel
    {
        static readonly Geometry CopperRim = Geometry.Parse("M0 0H120V62H91V80H29V62H0Z");
        static readonly Geometry OpticalRim = Geometry.Parse("M0 0H120V65H76V57H44V65H0Z");
        static readonly DrawingGroup CopperFace = MakeFace(false), OpticalFace = MakeFace(true);
        readonly bool optical;
        readonly Layer light;
        int previous = -1;
        public Socket(bool sfp)
        {
            optical = sfp;
            MaxWidth = sfp ? 144 : 128;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            light = new Layer(sfp, false);
            Children.Add(light);
            Children.Add(new Layer(sfp, true));
        }
        static IBrush Metal(string high, string low) => new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(.2, 1, RelativeUnit.Relative),
            GradientStops = [new GradientStop(Color.Parse(high), 0), new GradientStop(Color.Parse(low), .7), new GradientStop(Color.Parse(high), 1)]
        };
        static DrawingGroup MakeFace(bool sfp)
        {
            var group = new DrawingGroup();
            void Shape(string d, IBrush fill) => group.Children.Add(new GeometryDrawing { Geometry = Geometry.Parse(d), Brush = fill });
            if (sfp)
            {
                Shape("M2 2H118V63H78V55H42V63H2Z", Metal("#88919e", "#3b4350"));
                Shape("M12 9H108V51H12Z", Metal("#080b10", "#141b25"));
                Shape("M12 9H108L96 17H24Z", Metal("#7c8592", "#343b48"));
                for (int i = 0; i < 13; i++)
                {
                    int x = 32 + i * 4;
                    Shape($"M{x} 41h2v4h-2Z", U.Brush("#807354"));
                }
            }
            else
            {
                Shape("M2 2H118V60H89V78H31V60H2Z", Metal("#8f99a7", "#303947"));
                Shape("M9 6H111V58H85V74H35V58H9Z", Metal("#080b10", "#13202a"));
                Shape("M9 6H111L99 16H21Z", Metal("#747f8d", "#303b4a"));
                Shape("M9 7L21 16V50L9 58Z", Metal("#7b8797", "#273342"));
                Shape("M111 7L99 16V50L111 58Z", Metal("#7b8797", "#273342"));
                var contact = Metal("#807553", "#c1b58a");
                for (var n = 0; n < 8; n++)
                {
                    var x = 26 + n * 9;
                    Shape($"M{x} 16h5v15l-2 5h-3Z", contact);
                }
                Shape("M38 49H82V72H38Z", Metal("#0b111a", "#3d4a5c"));
                Shape("M40 50H80V59H40Z", U.Brush("#0c131b"));
            }
            return group;
        }
        protected override Size MeasureOverride(Size available)
        {
            var width = Math.Min(double.IsFinite(available.Width) ? available.Width : 128, MaxWidth);
            var size = new Size(width, width * (optical ? 65 : 80) / 120);
            foreach (var child in Children)
                child.Measure(size);
            return size;
        }
        protected override Size ArrangeOverride(Size final)
        {
            foreach (var child in Children)
                child.Arrange(new Rect(final));
            return final;
        }
        static Matrix Placement(bool optical, Size size)
        {
            var height = optical ? 65 : 80;
            var scale = Math.Min(size.Width / 120, size.Height / height);
            return Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((size.Width - 120 * scale) / 2, (size.Height - height * scale) / 2);
        }
        // The metal face never changes, but drawing on the processor redraws everything over a changed LED. Its
        // gradient shapes are drawn once per socket size and display scale into a bitmap that every tile and every
        // color change reuses.
        static readonly Dictionary<(bool Optical, double Width, double Height, double Scaling), RenderTargetBitmap> Faces = [];
        static RenderTargetBitmap? Face(bool optical, Size size, double scaling)
        {
            var key = (optical, size.Width, size.Height, scaling);
            if (Faces.TryGetValue(key, out var cached))
                return cached;
            var pixels = new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling));
            if (pixels.Width <= 0 || pixels.Height <= 0)
                return null;
            if (Faces.Count >= 16)
            {
                // Sizes from earlier window widths. Tiles redraw with the sizes still in use.
                foreach (var old in Faces.Values)
                    old.Dispose();
                Faces.Clear();
            }
            // The bitmap is plain pixels (96 DPI); the display scale is applied here and undone when it is drawn.
            var bitmap = new RenderTargetBitmap(pixels);
            using (var context = bitmap.CreateDrawingContext())
            using (context.PushTransform(Placement(optical, size) * Matrix.CreateScale(scaling, scaling)))
                (optical ? OpticalFace : CopperFace).Draw(context);
            Faces[key] = bitmap;
            return bitmap;
        }
        sealed class Layer(bool optical, bool metal) : Control
        {
            public IBrush Led = U.Brush("#555968");
            public override void Render(DrawingContext context)
            {
                base.Render(context);
                if (Bounds.Width <= 0 || Bounds.Height <= 0)
                    return;
                if (metal)
                {
                    if (Face(optical, Bounds.Size, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) is { } face)
                        context.DrawImage(face, new Rect(0, 0, face.PixelSize.Width, face.PixelSize.Height), new Rect(Bounds.Size));
                    return;
                }
                using (context.PushTransform(Placement(optical, Bounds.Size)))
                    context.DrawGeometry(Led, null, optical ? OpticalRim : CopperRim);
            }
        }
        public int Repaints { get; private set; }
        public void Set(double[] rgb)
        {
            static byte Chan(double d) => (byte)Math.Clamp((int)Math.Round(d), 0, 255);
            var r = Chan(rgb[0]);
            var g = Chan(rgb[1]);
            var b = Chan(rgb[2]);
            var key = r << 16 | g << 8 | b;
            if (previous == key)
                return;
            previous = key;
            light.Led = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(r, g, b));
            light.InvalidateVisual();
            Repaints++;
        }
    }
}
