using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages.Settings;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Fans;

static class Registration
{
    [ModuleInitializer]
    internal static void Register() => PageRegistry.Register("Fans", shell => new FansPage(shell));
}

/// <summary>
/// The Fans page: live switch temperatures and fan speeds, and the fan mode. The engine runs the chosen mode on the
/// switch (/api/fans), so the fans keep following it while EE is closed.
/// </summary>
public sealed class FansPage : Page
{
    static readonly (string Mode, string Label)[] Modes =
        [("switch", "Switch control"), ("curve", "Curve"), ("manual", "Manual"), ("target", "Thermal target")];
    static readonly string[] CurveOrder = ["low", "medium", "high", "max"];

    readonly Dictionary<string, Button> modeButtons = [];
    readonly Dictionary<string, Button> curveButtons = [];
    readonly Dictionary<string, TextBlock> readings = [];
    readonly Dictionary<string, TextBlock> labels = [];
    readonly TextBlock status = SettingsKit.P("", "muted");
    readonly TextBlock error = SettingsKit.P("", "danger");
    readonly ContentControl details = new();
    readonly Grid layout = new();
    readonly StackPanel left = new() { Spacing = 14 };
    readonly StackPanel right = new() { Spacing = 14 };
    readonly Slider manual = U.Range(30, 100, 1, 60);
    readonly Slider target = U.Range(45, 85, 1, 55);
    readonly TextBlock manualValue = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock targetValue = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly FanCurveChart chart = new();
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    readonly Control unavailable;
    readonly Control page;
    readonly TextBlock footer;
    readonly TextBlock subtitle;
    bool rack;
    JsonObject? pendingChange;
    string shownMode = "";
    bool sending, syncing, wide;

    public FansPage(AppShell shell) : base(shell)
    {
        foreach (var (mode, label) in Modes)
        {
            var button = U.Button(label, "quiet");
            button.Click += (_, _) => Choose(new JsonObject { ["mode"] = mode });
            modeButtons[mode] = button;
        }
        var modeRow = new UniformGrid { Rows = 1 };
        foreach (var button in modeButtons.Values)
        {
            button.BorderThickness = new Thickness(0);
            button.Margin = new Thickness(1.5, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            modeRow.Children.Add(button);
        }
        var modeBar = new Border { Child = modeRow, Padding = new Thickness(3), CornerRadius = new CornerRadius(7) }.Bind(Border.BackgroundProperty, "editor-tab-surface");
        AutomationProperties.SetName(modeBar, "Fan mode");

        foreach (var name in CurveOrder)
        {
            var button = U.Button(name, "quiet");
            button.Click += (_, _) => Choose(new JsonObject { ["mode"] = "curve", ["curve"] = name });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            curveButtons[name] = button;
        }

        manual.ValueChanged += (_, _) => { manualValue.Text = $"{(int)manual.Value}%"; Queue("manual_percent", (int)manual.Value, "manual"); };
        target.ValueChanged += (_, _) => { targetValue.Text = $"{(int)target.Value} °C"; Queue("target_celsius", (int)target.Value, "target"); };
        AutomationProperties.SetName(manual, "Fan speed");
        AutomationProperties.SetName(target, "Target CPU temperature");
        debounce.Tick += (_, _) => { debounce.Stop(); if (pendingChange is { } change) { pendingChange = null; Choose(change); } };

        // The fan tile also lists both fan speeds, so it gets more room.
        var readingsPanel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,1.6*") };
        foreach (var (key, label) in new[] { ("cpu", "CPU"), ("sfp", "SFP"), ("port", "Port chips"), ("fans", "Fans") })
        {
            var tile = Tile(key, label);
            Grid.SetColumn(tile, readingsPanel.Children.Count);
            readingsPanel.Children.Add(tile);
        }
        left.Children.Add(SettingsKit.SectionHeading("Readings"));
        left.Children.Add(readingsPanel);
        left.Children.Add(status);
        left.Children.Add(chart);
        right.Children.Add(SettingsKit.SectionHeading("Fan mode"));
        right.Children.Add(modeBar);
        right.Children.Add(details);
        right.Children.Add(error);
        right.Children.Add(footer = SettingsKit.HelpText(
            "Every mode speeds the fans up when the CPU passes 85 °C, the SFP sensor passes 65 °C or a port chip passes 100 °C. "
            + "Fan control runs on the switch and continues while EE is closed. A switch restart returns the fans to the switch’s own control until EE connects again."));

        layout.Children.Add(left);
        layout.Children.Add(right);
        var column = new StackPanel();
        var header = SettingsKit.ViewHeader("Fans", "Choose how the switch fans run. The readings refresh every 5 seconds.");
        subtitle = (TextBlock)((StackPanel)header.Children[0]).Children[1];
        column.Children.Add(header);
        column.Children.Add(SettingsKit.LeftMax(layout, 1180));
        page = column;
        unavailable = U.EmptyState("Fan control is not available", "");
        SizeChanged += (_, e) => { if (e.WidthChanged) Arrange(e.NewSize.Width); };
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) top.PropertyChanged += (_, e) => { if (e.Property == TopLevel.ClientSizeProperty) Fit(); };
            Fit();
        };
        Arrange(960);
        Update();
    }

    /// <summary>A rack window (such as 1280 × 400) drops the chart and the longer help text so nothing scrolls.</summary>
    void Fit()
    {
        var height = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 900;
        var next = height < 520;
        if (next == rack) return;
        rack = next;
        footer.IsVisible = !rack;
        subtitle.IsVisible = !rack;
        ToolTip.SetTip(details, rack ? footer.Text : null);
        shownMode = "";
        Update();
    }

    Control Tile(string key, string label)
    {
        var value = new TextBlock { Text = "–", FontSize = 19, FontWeight = FontWeight.SemiBold };
        readings[key] = value;
        var body = new StackPanel { Spacing = 2 };
        body.Children.Add(new TextBlock { Text = label, FontSize = 12 }.Bind(TextBlock.ForegroundProperty, "muted"));
        body.Children.Add(value);
        if (key == "fans") labels[key] = (TextBlock)body.Children[0];
        return new Border { Child = body, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 8, 0) }.Bind(Border.BackgroundProperty, "inset");
    }

    /// <summary>Two columns when the window is wide enough, so a rack window shows everything without scrolling.</summary>
    void Arrange(double width)
    {
        var next = width >= 1000;
        if (next == wide && layout.ColumnDefinitions.Count > 0) return;
        wide = next;
        layout.ColumnDefinitions = wide ? new ColumnDefinitions("*,28,*") : new ColumnDefinitions("*");
        layout.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
        Grid.SetColumn(right, wide ? 2 : 0);
        Grid.SetRow(right, wide ? 0 : 1);
        right.Margin = wide ? default : new Thickness(0, 22, 0, 0);
        chart.Height = wide ? 150 : 170;
    }

    JsonObject? Fans => Workspace.State["fans"] as JsonObject;

    public override void Update()
    {
        var fans = Fans;
        if (fans is null || fans.Bool("supported") != true)
        {
            if (unavailable is StackPanel { Children: [_, TextBlock text, ..] })
                text.Text = fans?.Str("unsupported_reason") ?? (Workspace.Connected ? "Fan control needs a USW-Pro-XG-8-PoE connected to EE." : "Connect to the switch to see its fans.");
            if (!ReferenceEquals(Content, unavailable)) Content = unavailable;
            return;
        }
        if (!ReferenceEquals(Content, page)) Content = page;
        var settings = fans["settings"] as JsonObject ?? [];
        var mode = settings.Str("mode") ?? "switch";
        syncing = true;
        try
        {
            if (!manual.IsPointerOver && pendingChange is null) manual.Value = settings.Int("manual_percent") ?? 60;
            if (!target.IsPointerOver && pendingChange is null) target.Value = settings.Int("target_celsius") ?? 55;
        }
        finally { syncing = false; }
        manualValue.Text = $"{(int)manual.Value}%";
        targetValue.Text = $"{(int)target.Value} °C";
        foreach (var (key, button) in modeButtons)
        {
            button.Classes.Set("selected", key == mode);
            button.IsEnabled = !sending;
            AutomationProperties.SetHelpText(button, key == mode ? "Selected" : "");
        }
        var curves = fans["curves"] as JsonObject;
        foreach (var (name, button) in curveButtons)
        {
            var selected = mode == "curve" && settings.Str("curve") == name;
            button.Classes.Set("selected", selected);
            button.IsEnabled = !sending;
            button.Content = CurveLabel(name, curves?[name] as JsonObject, rack);
        }
        if (shownMode != mode)
        {
            shownMode = mode;
            details.Content = Details(mode);
        }
        error.Text = fans.Str("error") ?? "";
        error.IsVisible = error.Text.Length > 0;
        chart.Curves = curves;
        chart.IsVisible = mode == "curve" && !rack;
        chart.Selected = mode == "curve" ? settings.Str("curve") : null;
        Frame();
    }

    public override void Frame()
    {
        if (Fans is not { } fans || fans.Bool("supported") != true) return;
        var reading = fans["reading"] as JsonObject;
        string Celsius(string key) => reading?.Num(key) is { } value ? value.ToString("0.0", CultureInfo.CurrentCulture) + " °C" : "–";
        readings["cpu"].Text = Celsius("cpu_celsius");
        readings["sfp"].Text = Celsius("sfp_celsius");
        readings["port"].Text = Celsius("port_celsius");
        var fanList = reading?["fans"] as JsonArray;
        var speeds = fanList?.Select(fan => fan.Num("rpm")).OfType<double>().Select(rpm => rpm.ToString("N0", CultureInfo.CurrentCulture)).ToList() ?? [];
        readings["fans"].Text = fanList is { Count: > 0 } && fanList[0].Int("percent") is { } percent ? $"{percent}%" : "–";
        labels["fans"].Text = speeds.Count > 0 ? $"Fans · {string.Join(" / ", speeds)} RPM" : "Fans";
        status.Text = Describe(fans);
        chart.Marker = reading?.Num("cpu_celsius") is { } cpu && fanList is { Count: > 0 } && fanList[0].Num("percent") is { } duty ? new Point(cpu, duty) : null;
    }

    static string Describe(JsonObject fans)
    {
        var reading = fans["reading"] as JsonObject;
        var settings = fans["settings"] as JsonObject;
        if (reading is null) return "Waiting for a reading from the switch.";
        var mode = settings.Str("mode");
        if (mode == "switch")
            return reading.Str("daemon") == "paused" ? "The switch’s fan controller is paused by another tool." : "The switch’s own fan controller sets the speed.";
        if (reading.Str("controller") != "running") return "Starting fan control on the switch.";
        var duty = reading.Int("duty") is { } value ? $" Fans at {value}%." : "";
        return reading.Str("reason") switch
        {
            "guard" => "A sensor is near the switch’s limit, so the fans are running faster than the mode asks." + duty,
            "fault" => "A sensor could not be read, so the fans are at full speed.",
            "target" => $"Keeping the CPU at {settings.Int("target_celsius")} °C or below.{duty}",
            "manual" => $"Both fans fixed at {settings.Int("manual_percent")}%.",
            "curve" => $"Following the {Title(settings.Str("curve"))} curve.{duty}",
            _ => "Fan control is running on the switch." + duty,
        };
    }

    static string Title(string? name) => name is { Length: > 0 } ? char.ToUpperInvariant(name[0]) + name[1..] : "";

    static Control CurveLabel(string name, JsonObject? curve, bool brief)
    {
        var points = (curve?["points"] as JsonArray)?.Select(p => (T: p?[0]?.GetValue<int>() ?? 0, D: p?[1]?.GetValue<int>() ?? 0)).ToList() ?? [];
        var full = points.FirstOrDefault(p => p.D >= 100);
        var text = points.Count < 2 ? ""
            : brief ? $"{points[0].D}–100%, {points[0].T}–{full.T} °C"
            : $"{points[0].D}% at {points[0].T} °C, 100% at {full.T} °C";
        var body = new StackPanel { Spacing = 1 };
        body.Children.Add(new TextBlock { Text = curve.Str("name") ?? Title(name), FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = text, FontSize = 12, Opacity = 0.8 });
        return body;
    }

    Control Details(string mode)
    {
        switch (mode)
        {
            case "curve" when rack:
            {
                var row = new UniformGrid { Columns = 4 };
                foreach (var name in CurveOrder)
                {
                    curveButtons[name].Margin = new Thickness(0, 0, 6, 0);
                    if (curveButtons[name].Parent is Panel old) old.Children.Remove(curveButtons[name]);
                    row.Children.Add(curveButtons[name]);
                }
                return row;
            }
            case "curve":
            {
                var grid = new UniformGrid { Columns = 2 };
                foreach (var name in CurveOrder)
                {
                    curveButtons[name].Margin = new Thickness(0, 0, 6, 6);
                    if (curveButtons[name].Parent is Panel old) old.Children.Remove(curveButtons[name]);
                    grid.Children.Add(curveButtons[name]);
                }
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(grid);
                panel.Children.Add(SettingsKit.HelpText("The fan speed follows the CPU temperature along the selected curve. Higher curves run the fans faster and the switch cooler."));
                return panel;
            }
            case "manual":
                return SliderBlock(manual, manualValue, "Both fans run at this speed.");
            case "target":
                return SliderBlock(target, targetValue, rack ? "The fans run as slowly as possible while keeping the CPU at or below this temperature."
                    : "The fans run as slowly as possible while keeping the CPU at or below this temperature. In a warm room the fans may reach full speed before the CPU reaches the target.");
            default:
                return SettingsKit.HelpText("The switch’s own fan controller sets the speed. EE does not change the fans.");
        }
    }

    static Control SliderBlock(Slider slider, TextBlock value, string help)
    {
        if (slider.Parent is Panel old) old.Children.Remove(slider);
        if (value.Parent is Panel oldValue) oldValue.Children.Remove(value);
        var row = new DockPanel();
        DockPanel.SetDock(value, Dock.Right);
        row.Children.Add(value);
        row.Children.Add(slider);
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(row);
        panel.Children.Add(SettingsKit.HelpText(help));
        return panel;
    }

    void Queue(string key, int value, string mode)
    {
        if (syncing) return;
        pendingChange = new JsonObject { ["mode"] = mode, [key] = value };
        debounce.Stop();
        debounce.Start();
    }

    async void Choose(JsonObject change)
    {
        if (sending) { pendingChange = change; return; }
        sending = true;
        Update();
        try
        {
            var result = await Workspace.Request("/api/fans", "PUT", change);
            if (result is JsonObject fans) Workspace.State["fans"] = fans.DeepClone();
        }
        catch (Exception exception)
        {
            Shell.Notify(exception.Message is { Length: > 0 } message ? message : "The switch did not confirm the fan setting.", true);
            try
            {
                if (await Workspace.Request("/api/fans") is JsonObject fans) Workspace.State["fans"] = fans.DeepClone();
            }
            catch (Exception) { }
        }
        finally
        {
            sending = false;
            if (pendingChange is not null && !debounce.IsEnabled) { var next = pendingChange; pendingChange = null; Choose(next); }
            else Update();
        }
    }

    public override void Leave()
    {
        debounce.Stop();
        if (pendingChange is { } change) { pendingChange = null; Choose(change); }
    }
}

/// <summary>The four fan curves on a temperature and fan speed grid, with the current reading as a dot.</summary>
internal sealed class FanCurveChart : Control
{
    public static readonly StyledProperty<IBrush?> AccentProperty = AvaloniaProperty.Register<FanCurveChart, IBrush?>(nameof(Accent));
    public static readonly StyledProperty<IBrush?> LineProperty = AvaloniaProperty.Register<FanCurveChart, IBrush?>(nameof(Line));
    public static readonly StyledProperty<IBrush?> FaintProperty = AvaloniaProperty.Register<FanCurveChart, IBrush?>(nameof(Faint));
    public static readonly StyledProperty<IBrush?> MarkProperty = AvaloniaProperty.Register<FanCurveChart, IBrush?>(nameof(Mark));
    const double MinT = 30, MaxT = 95;
    JsonObject? curves;
    string? selected;
    Point? marker;

    public FanCurveChart()
    {
        this.Bind(AccentProperty, "accent").Bind(LineProperty, "line").Bind(FaintProperty, "faint").Bind(MarkProperty, "pink");
        AffectsRender<FanCurveChart>(AccentProperty, LineProperty, FaintProperty, MarkProperty);
        AutomationProperties.SetName(this, "Fan curves");
    }

    public IBrush? Accent { get => GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public IBrush? Line { get => GetValue(LineProperty); set => SetValue(LineProperty, value); }
    public IBrush? Faint { get => GetValue(FaintProperty); set => SetValue(FaintProperty, value); }
    public IBrush? Mark { get => GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

    public JsonObject? Curves { set { if (!JsonNode.DeepEquals(curves, value)) { curves = (JsonObject?)value?.DeepClone(); InvalidateVisual(); } } }
    public string? Selected { set { if (selected != value) { selected = value; InvalidateVisual(); } } }
    public Point? Marker { set { if (marker != value) { marker = value; InvalidateVisual(); } } }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size).Deflate(new Thickness(34, 8, 8, 20));
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        Point Map(double t, double d) => new(bounds.X + (Math.Clamp(t, MinT, MaxT) - MinT) / (MaxT - MinT) * bounds.Width, bounds.Bottom - d / 100 * bounds.Height);
        var grid = new Pen(Line, 1);
        var label = Faint ?? Brushes.Gray;
        foreach (var d in new[] { 0, 50, 100 })
        {
            context.DrawLine(grid, Map(MinT, d), Map(MaxT, d));
            Text(context, $"{d}%", new Point(bounds.X - 30, Map(MinT, d).Y - 7), label);
        }
        foreach (var t in new[] { 40, 60, 80 })
            Text(context, $"{t} °C", new Point(Map(t, 0).X - 14, bounds.Bottom + 4), label);
        if (curves is not null)
            foreach (var (name, curve) in curves)
            {
                if ((curve?["points"] as JsonArray) is not { Count: >= 2 } points) continue;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var first = points[0]!;
                    g.BeginFigure(Map(MinT, first[1]!.GetValue<int>()), false);
                    foreach (var point in points) g.LineTo(Map(point![0]!.GetValue<int>(), point[1]!.GetValue<int>()));
                    g.LineTo(Map(MaxT, 100));
                    g.EndFigure(false);
                }
                var isSelected = name == selected;
                context.DrawGeometry(null, new Pen(isSelected ? Accent : Faint, isSelected ? 2.4 : 1.2) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geometry);
            }
        if (marker is { } m)
            context.DrawEllipse(Mark, null, Map(m.X, m.Y), 4.5, 4.5);
    }

    static void Text(DrawingContext context, string text, Point at, IBrush brush) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(Look.Font), 10.5, brush), at);
}
