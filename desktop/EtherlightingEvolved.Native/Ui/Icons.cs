using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EtherlightingEvolved.Native.Ui;

/// <summary>
/// The workspace icon set, read from the browser workspace's icons.mjs: 24-unit outline drawings stroked at 1.6
/// with round caps and joins, in the current text color.
/// </summary>
public static class Icons
{
    static readonly Lazy<Dictionary<string, Geometry>> Shapes = new(Load);

    public static bool Has(string name) => Shapes.Value.ContainsKey(name);

    public static Geometry Get(string name) => Shapes.Value.TryGetValue(name, out var geometry) ? geometry : Shapes.Value["sparkles"];

    static Dictionary<string, Geometry> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icons.mjs")
                           ?? throw new InvalidOperationException("The icons.mjs resource is missing.");
        var source = new StreamReader(stream).ReadToEnd();
        var result = new Dictionary<string, Geometry>(StringComparer.Ordinal);
        foreach (Match entry in Regex.Matches(source, @"(\w+):\s*'([^']*)'"))
        {
            var group = new GeometryGroup { FillRule = FillRule.NonZero };
            foreach (Match element in Regex.Matches(entry.Groups[2].Value, @"<(path|rect|circle)\s([^>]*?)/?>"))
            {
                var attributes = Regex.Matches(element.Groups[2].Value, @"([\w-]+)=""([^""]*)""").ToDictionary(match => match.Groups[1].Value, match => match.Groups[2].Value);
                double Number(string key) => attributes.TryGetValue(key, out var text) ? double.Parse(text, CultureInfo.InvariantCulture) : 0;
                Geometry? shape = element.Groups[1].Value switch
                {
                    "path" when attributes.TryGetValue("d", out var data) => StreamGeometry.Parse(data),
                    "rect" => new RectangleGeometry(new Rect(Number("x"), Number("y"), Number("width"), Number("height")), Number("rx"), Number("rx")),
                    "circle" => new EllipseGeometry(new Rect(Number("cx") - Number("r"), Number("cy") - Number("r"), Number("r") * 2, Number("r") * 2)),
                    _ => null,
                };
                if (shape is null) continue;
                if (attributes.TryGetValue("transform", out var transform) && Regex.Match(transform, @"rotate\(([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\)") is { Success: true } rotate)
                {
                    var angle = double.Parse(rotate.Groups[1].Value, CultureInfo.InvariantCulture);
                    var x = double.Parse(rotate.Groups[2].Value, CultureInfo.InvariantCulture);
                    var y = double.Parse(rotate.Groups[3].Value, CultureInfo.InvariantCulture);
                    shape.Transform = new MatrixTransform(Matrix.CreateTranslation(-x, -y) * Matrix.CreateRotation(angle * Math.PI / 180) * Matrix.CreateTranslation(x, y));
                }
                group.Children.Add(shape);
            }
            result[entry.Groups[1].Value] = group;
        }
        return result;
    }
}

/// <summary>An outline icon from the workspace set. Its stroke follows Foreground, like currentColor on the web.</summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string> NameKeyProperty = AvaloniaProperty.Register<Icon, string>(nameof(NameKey), "sparkles");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = Avalonia.Controls.Documents.TextElement.ForegroundProperty.AddOwner<Icon>();
    public static readonly StyledProperty<double> StrokeWidthProperty = AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 1.6);

    static Icon() => AffectsRender<Icon>(NameKeyProperty, ForegroundProperty, StrokeWidthProperty);

    public Icon() { Width = 20; Height = 20; }

    public Icon(string name, double size = 20) : this()
    {
        NameKey = name;
        Width = Height = size;
    }

    public string NameKey { get => GetValue(NameKeyProperty); set => SetValue(NameKeyProperty, value); }

    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public double StrokeWidth { get => GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public override void Render(DrawingContext context)
    {
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        if (scale <= 0 || Foreground is null) return;
        var pen = new Pen(Foreground, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2)))
            context.DrawGeometry(null, pen, Icons.Get(NameKey));
    }
}
