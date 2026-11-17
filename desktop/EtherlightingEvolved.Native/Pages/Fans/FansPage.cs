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
/// A choice shows at once and is sent in the background; the newest choice wins if several are made quickly.
/// A rack window (such as 1280 × 400) gets a fixed layout that fills the screen with large readings and controls.
/// </summary>
public sealed class FansPage : Page
{
    static readonly (string Mode, string Label)[] Modes =
        [("switch", "Switch control"), ("curve", "Curve"), ("manual", "Manual"), ("target", "Thermal target")];
    static readonly string[] CurveOrder = ["low", "medium", "high", "max"];
    const string Footer = "Every mode speeds the fans up when the CPU passes 85 °C, the SFP sensor passes 65 °C or a port chip passes 100 °C. "
                          + "EE saves the fan mode for each switch; a switch EE has not controlled before starts in Switch control. "
                          + "Fan control runs on the switch and continues while EE is closed. Handoff to UniFi and a switch restart return the fans to the switch’s own control until EE connects again.";

    readonly Dictionary<string, Button> modeButtons = [];
    readonly Dictionary<string, Button> curveButtons = [];
    readonly Dictionary<string, TextBlock> readings = [];
    readonly Dictionary<string, TextBlock> labels = [];
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    TextBlock status = new(), error = new(), manualValue = new(), targetValue = new();
    ContentControl details = new();
    Slider manual = new(), target = new();
    FanCurveChart chart = new();
    Grid? columns;
    Control? unavailable, page;
    /// <summary>Settings as chosen on this page, shown until the switch confirms them.</summary>
    JsonObject? chosen;
    string shownMode = "";
    bool sending, resend, syncing, rack, wide, failed;

    public FansPage(AppShell shell) : base(shell)
    {
        debounce.Tick += (_, _) => { debounce.Stop(); Send(); };
        SizeChanged += (_, e) => { if (e.WidthChanged) Arrange(e.NewSize.Width); };
        Build();
        Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is TopLevel top) top.PropertyChanged += OnTopLevelChanged;
        Fit();
    }

    // The window outlives this page, so its handler is removed when the page leaves. The page no longer reaches the
    // window through its own tree at this point, only through the event's root.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.Root is TopLevel top) top.PropertyChanged -= OnTopLevelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty) Fit();
    }

    /// <summary>The page lays itself out: it scrolls in a normal window and fills a rack window without scrolling.</summary>
    public override bool FullBleed => true;

    JsonObject? Fans => Workspace.State["fans"] as JsonObject;

    JsonObject Settings => chosen ?? Fans?["settings"] as JsonObject ?? [];

    void Fit()
    {
        var height = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 900;
        var next = height < 520;
        if (next == rack) return;
        rack = next;
        Build();
        Update();
    }

    // ---- Layout

    void Build()
    {
        modeButtons.Clear();
        curveButtons.Clear();
        readings.Clear();
        labels.Clear();
        double Size(double normal, double large) => rack ? large : normal;

        status = new TextBlock { FontSize = Size(13, 15), LineHeight = Size(19.5, 21), TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
        error = new TextBlock { FontSize = Size(13, 15), TextWrapping = TextWrapping.Wrap, IsVisible = false }.Bind(TextBlock.ForegroundProperty, "danger");
        foreach (var (mode, label) in Modes)
        {
            var button = U.Button(label, "quiet");
            button.Click += (_, _) => Choose(ModeChange(mode));
            button.BorderThickness = new Thickness(0);
            button.Margin = new Thickness(1.5, 0);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.FontSize = Size(13, 17);
            button.MinHeight = Size(0, 52);
            modeButtons[mode] = button;
        }
        foreach (var name in CurveOrder)
        {
            // Plain buttons, so each curve reads as something to press, not as a label.
            var button = U.Button(name, null);
            button.Click += (_, _) => Choose(new JsonObject { ["mode"] = "curve", ["curve"] = name });
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.VerticalAlignment = VerticalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.Padding = rack ? new Thickness(18, 10) : new Thickness(12, 7);
            curveButtons[name] = button;
        }
        manual = U.Range(30, 100, 1, Settings.Int("manual_percent") ?? 60);
        target = U.Range(45, 85, 1, Settings.Int("target_celsius") ?? 55);
        AutomationProperties.SetName(manual, "Fan speed");
        AutomationProperties.SetName(target, "Target CPU temperature");
        manual.ValueChanged += (_, _) => Slid("manual", "manual_percent", (int)manual.Value);
        target.ValueChanged += (_, _) => Slid("target", "target_celsius", (int)target.Value);
        manualValue = new TextBlock { FontSize = Size(15, 40), FontWeight = FontWeight.SemiBold, MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        targetValue = new TextBlock { FontSize = Size(15, 40), FontWeight = FontWeight.SemiBold, MinWidth = 52, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        chart = new FanCurveChart { Height = 150 };
        details = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        unavailable = U.EmptyState("Fan control is not available", "");
        shownMode = "";
        page = rack ? RackLayout() : NormalLayout();
        Content = page;
    }

    Border ModeBar()
    {
        var row = new UniformGrid { Rows = 1 };
        foreach (var button in modeButtons.Values) row.Children.Add(button);
        var bar = new Border { Child = row, Padding = new Thickness(3), CornerRadius = new CornerRadius(rack ? 9 : 7) }.Bind(Border.BackgroundProperty, "editor-tab-surface");
        AutomationProperties.SetName(bar, "Fan mode");
        return bar;
    }

    Border Tile(string key, string label)
    {
        var value = new TextBlock { Text = "–", FontSize = rack ? 36 : 19, FontWeight = FontWeight.SemiBold };
        var name = new TextBlock { Text = label, FontSize = rack ? 15 : 12, TextTrimming = TextTrimming.CharacterEllipsis }.Bind(TextBlock.ForegroundProperty, "muted");
        readings[key] = value;
        labels[key] = name;
        var body = new StackPanel { Spacing = rack ? 4 : 2, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(name);
        body.Children.Add(value);
        return new Border { Child = body, Padding = rack ? new Thickness(18, 10) : new Thickness(12, 8), CornerRadius = new CornerRadius(rack ? 12 : 8) }
            .Bind(Border.BackgroundProperty, "inset");
    }

    /// <summary>A normal window: header, two columns when wide enough, help text, scrolling as other secondary pages do.</summary>
    Control NormalLayout()
    {
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,1.6*"), ColumnSpacing = 8 };
        foreach (var (key, label) in new[] { ("cpu", "CPU"), ("sfp", "SFP"), ("port", "Port chips"), ("fans", "Fans") })
        {
            var tile = Tile(key, label);
            Grid.SetColumn(tile, tiles.Children.Count);
            tiles.Children.Add(tile);
        }
        var left = new StackPanel { Spacing = 14 };
        left.Children.Add(SettingsKit.SectionHeading("Readings"));
        left.Children.Add(tiles);
        left.Children.Add(status);
        left.Children.Add(chart);
        var right = new StackPanel { Spacing = 14 };
        right.Children.Add(SettingsKit.SectionHeading("Fan mode"));
        right.Children.Add(ModeBar());
        right.Children.Add(details);
        right.Children.Add(error);
        right.Children.Add(U.HelpText(Footer));
        columns = new Grid();
        columns.Children.Add(left);
        columns.Children.Add(right);
        Arrange(Bounds.Width > 0 ? Bounds.Width : 960, force: true);
        var column = new StackPanel();
        column.Children.Add(SettingsKit.ViewHeader("Fans", "Choose how the switch fans run. The readings refresh every few seconds."));
        column.Children.Add(SettingsKit.LeftMax(columns, 1180));
        return new ScrollViewer
        {
            Content = new Border { Padding = new Thickness(24), Child = column }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    /// <summary>Two columns when the window is wide enough.</summary>
    void Arrange(double width, bool force = false)
    {
        if (rack || columns is not { Children: [Control left, Control right] }) return;
        var next = width >= 1000;
        if (next == wide && !force) return;
        wide = next;
        columns.ColumnDefinitions = wide ? new ColumnDefinitions("*,28,*") : new ColumnDefinitions("*");
        columns.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, wide ? 2 : 0);
        Grid.SetRow(right, wide ? 0 : 1);
        right.Margin = wide ? default : new Thickness(0, 22, 0, 0);
    }

    /// <summary>
    /// A rack window: readings fill the left side in a two by two grid, the mode buttons and the chosen mode's controls
    /// fill the right side. Nothing scrolls. The longer help stays in the normal layout and on the Help page.
    /// </summary>
    Control RackLayout()
    {
        var tiles = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("*,*,Auto"), ColumnSpacing = 12, RowSpacing = 12,
        };
        var index = 0;
        foreach (var (key, label) in new[] { ("cpu", "CPU"), ("sfp", "SFP"), ("port", "Port chips"), ("fans", "Fans") })
        {
            var tile = Tile(key, label);
            Grid.SetColumn(tile, index % 2);
            Grid.SetRow(tile, index / 2);
            tiles.Children.Add(tile);
            index++;
        }
        Grid.SetRow(status, 2);
        Grid.SetColumnSpan(status, 2);
        tiles.Children.Add(status);

        var right = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 12 };
        right.Children.Add(ModeBar());
        Grid.SetRow(details, 1);
        right.Children.Add(details);
        Grid.SetRow(error, 2);
        right.Children.Add(error);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("5*,6*"), ColumnSpacing = 22, Margin = new Thickness(18, 14, 18, 12) };
        root.Children.Add(tiles);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);
        return root;
    }

    Control Details(string mode)
    {
        foreach (var button in curveButtons.Values)
            if (button.Parent is Panel old) old.Children.Remove(button);
        switch (mode)
        {
            case "curve" when rack:
            {
                var grid = new UniformGrid { Columns = 2, Rows = 2 };
                foreach (var name in CurveOrder)
                {
                    curveButtons[name].Margin = new Thickness(0, 0, 10, 10);
                    grid.Children.Add(curveButtons[name]);
                }
                return grid;
            }
            case "curve":
            {
                var grid = new UniformGrid { Columns = 2 };
                foreach (var name in CurveOrder)
                {
                    curveButtons[name].Margin = new Thickness(0, 0, 6, 6);
                    grid.Children.Add(curveButtons[name]);
                }
                var panel = new StackPanel { Spacing = 8 };
                panel.Children.Add(grid);
                panel.Children.Add(U.HelpText("The fan speed follows the CPU temperature along the selected curve. Higher curves run the fans faster and the switch cooler."));
                return panel;
            }
            case "manual":
                return SliderBlock(manual, manualValue, 5, "Both fans run at this speed.");
            case "target":
                return SliderBlock(target, targetValue, 1, rack ? "The fans run as slowly as possible while keeping the CPU at or below this temperature."
                    : "The fans run as slowly as possible while keeping the CPU at or below this temperature. In a warm room the fans may reach full speed before the CPU reaches the target.");
            default:
                return rack
                    ? new TextBlock { Text = "The switch’s own fan controller sets the speed. EE does not change the fans.", FontSize = 16, LineHeight = 23, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
                        .Bind(TextBlock.ForegroundProperty, "muted")
                    : U.HelpText("The switch’s own fan controller sets the speed. EE does not change the fans.");
        }
    }

    Control SliderBlock(Slider slider, TextBlock value, int step, string help)
    {
        if (slider.Parent is Control parent) Detach(slider, parent);
        if (value.Parent is Panel oldValue) oldValue.Children.Remove(value);
        if (!rack)
        {
            var row = new DockPanel();
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(slider);
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(row);
            panel.Children.Add(U.HelpText(help));
            return panel;
        }
        // Large controls for the rack's touch screen: the value, then − slider + with the slider drawn at twice the size.
        Button Step(string text, int delta, string name)
        {
            var button = U.Button(text, null);
            button.FontSize = 26;
            button.Width = 64;
            button.Height = 54;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.Click += (_, _) => slider.Value = Math.Clamp(slider.Value + delta, slider.Minimum, slider.Maximum);
            AutomationProperties.SetName(button, name);
            return button;
        }
        value.HorizontalAlignment = HorizontalAlignment.Center;
        value.TextAlignment = TextAlignment.Center;
        var scaled = new LayoutTransformControl { Child = slider, LayoutTransform = new ScaleTransform(2.2, 2.2), VerticalAlignment = VerticalAlignment.Center };
        var controls = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 16 };
        controls.Children.Add(Step("−", -step, "Lower"));
        Grid.SetColumn(scaled, 1);
        controls.Children.Add(scaled);
        var plus = Step("+", step, "Higher");
        Grid.SetColumn(plus, 2);
        controls.Children.Add(plus);
        var hint = new TextBlock { Text = help, FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center }.Bind(TextBlock.ForegroundProperty, "muted");
        var block = new Grid { RowDefinitions = new RowDefinitions("*,Auto,Auto"), RowSpacing = 10 };
        block.Children.Add(value);
        Grid.SetRow(controls, 1);
        block.Children.Add(controls);
        Grid.SetRow(hint, 2);
        block.Children.Add(hint);
        return block;
    }

    static void Detach(Control child, Control parent)
    {
        if (parent is Panel panel) panel.Children.Remove(child);
        else if (parent is LayoutTransformControl transform) transform.Child = null;
        else if (parent is Decorator decorator) decorator.Child = null;
    }

    // ---- State

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
        var settings = Settings;
        var mode = settings.Str("mode") ?? "switch";
        // While EE leaves running fan control alone, the saved mode is not what runs, so no mode shows as selected.
        // A mode chosen here shows at once.
        var held = fans.Str("hold") is not null && chosen is null;
        // The service refuses every change while a newer EE's fan file is in place, since it never overwrites that file.
        var locked = fans.Str("hold") == "settings_newer";
        syncing = true;
        try
        {
            (manual.Minimum, manual.Maximum) = Limit(fans, "manual_percent", 30, 100);
            (target.Minimum, target.Maximum) = Limit(fans, "target_celsius", 45, 85);
            // A value being chosen is in chosen, so this never moves a slider out from under a finger or the mouse.
            manual.Value = settings.Int("manual_percent") ?? 60;
            target.Value = settings.Int("target_celsius") ?? 55;
        }
        finally { syncing = false; }
        manualValue.Text = $"{(int)manual.Value}%";
        targetValue.Text = $"{(int)target.Value} °C";
        foreach (var (key, button) in modeButtons)
        {
            button.Classes.Set("selected", key == mode && !held);
            AutomationProperties.SetHelpText(button, key == mode && !held ? "Selected" : "");
            button.IsEnabled = !locked;
        }
        var curves = fans["curves"] as JsonObject;
        foreach (var (name, button) in curveButtons)
        {
            button.Classes.Set("selected", !held && mode == "curve" && settings.Str("curve") == name);
            button.Content = CurveLabel(name, curves?[name] as JsonObject);
        }
        var shown = held ? "held" : mode;
        if (shownMode != shown)
        {
            shownMode = shown;
            details.Content = held ? null : Details(mode);
        }
        // settings_error stays set until a new mode is saved, so a lost fan choice remains visible after the switch
        // takes the fans back and error clears.
        var problem = fans.Str("error") is { Length: > 0 } failure ? failure : fans.Str("settings_error");
        error.Text = sending || chosen is not null ? "" : problem ?? "";
        error.IsVisible = error.Text.Length > 0;
        chart.Curves = curves;
        chart.IsVisible = !held && mode == "curve";
        chart.Selected = !held && mode == "curve" ? settings.Str("curve") : null;
        Frame();
    }

    public override void Frame()
    {
        if (Fans is not { } fans || fans.Bool("supported") != true || readings.Count == 0) return;
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

    string Describe(JsonObject fans)
    {
        var settings = Settings;
        if (sending || chosen is not null) return "Sending the new setting to the switch…";
        var reading = fans["reading"] as JsonObject;
        if (reading is null) return "Waiting for a reading from the switch.";
        switch (fans.Str("hold"))
        {
            case "other_installation":
                return "Fan control on this switch was not started from the fan settings saved on this PC, for example by EE on another PC with release 0.6.0 or later. EE leaves it running until you choose a mode here.";
            case "settings_unreadable":
                return "EE could not load the fan mode saved for this switch, so it does not change fan control that is running. Choose a mode to save it again.";
            case "settings_newer":
                return "The fan mode for this switch was saved by a newer version of EE. This version does not change fan control that is running and cannot change the mode. Handoff to UniFi in Settings still returns the fans to the switch’s own controller.";
        }
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

    /// <summary>The range the service allows for a setting, or this version's range when it sends none.</summary>
    static (double Min, double Max) Limit(JsonObject fans, string key, double min, double max) =>
        fans.Arr("limits." + key) is [var low, var high] && low.Num() is { } first && high.Num() is { } last && first < last ? (first, last) : (min, max);

    Control CurveLabel(string name, JsonObject? curve)
    {
        var points = FanCurveChart.Points(curve);
        var full = points.FirstOrDefault(p => p.D >= 100);
        var text = points.Count < 2 ? "" : $"{points[0].D}% at {points[0].T} °C, 100% at {full.T} °C";
        var body = new StackPanel { Spacing = rack ? 3 : 1, VerticalAlignment = VerticalAlignment.Center };
        body.Children.Add(new TextBlock { Text = curve.Str("name") ?? Title(name), FontWeight = FontWeight.SemiBold, FontSize = rack ? 20 : 13 });
        body.Children.Add(new TextBlock { Text = text, FontSize = rack ? 14 : 12, Opacity = 0.8, TextTrimming = TextTrimming.CharacterEllipsis });
        return body;
    }

    // ---- Changes

    /// <summary>
    /// Switching to Manual starts at the fans' current speed, so choosing the mode does not jump to the last saved
    /// speed; the slider then sets the speed from there.
    /// </summary>
    JsonObject ModeChange(string mode)
    {
        var change = new JsonObject { ["mode"] = mode };
        if (mode == "manual" && Settings.Str("mode") != "manual"
            && (Fans?["reading"]?["fans"] as JsonArray) is { Count: > 0 } fans && fans[0].Int("percent") is { } percent)
            change["manual_percent"] = Math.Clamp(percent, (int)manual.Minimum, (int)manual.Maximum);
        return change;
    }

    /// <summary>Shows a choice at once and sends it.</summary>
    void Choose(JsonObject change)
    {
        Remember(change);
        debounce.Stop();
        Update();
        Send();
    }

    /// <summary>A slider moved: show the value at once and send it after a short pause.</summary>
    void Slid(string mode, string key, int value)
    {
        if (syncing) return;
        Remember(new JsonObject { ["mode"] = mode, [key] = value });
        manualValue.Text = $"{(int)manual.Value}%";
        targetValue.Text = $"{(int)target.Value} °C";
        if (Fans is { } fans) status.Text = Describe(fans);
        debounce.Stop();
        debounce.Start();
    }

    void Remember(JsonObject change)
    {
        var next = (JsonObject)Settings.DeepClone();
        foreach (var (key, value) in change) next[key] = value?.DeepClone();
        chosen = next;
    }

    /// <summary>Sends the newest choice. A choice made while one is on its way is sent when that one returns.</summary>
    async void Send()
    {
        if (chosen is null) return;
        if (sending) { resend = true; return; }
        sending = true;
        failed = false;
        var body = (JsonObject)chosen.DeepClone();
        try
        {
            if (await Workspace.Request("/api/fans", "PUT", body) is JsonObject fans) Workspace.State["fans"] = fans.DeepClone();
        }
        catch (Exception exception)
        {
            failed = true;
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
            if (resend && !failed)
            {
                resend = false;
                Send();
            }
            else
            {
                resend = false;
                // Show what the switch reports, unless a newer choice is still waiting for its pause to end.
                if (!debounce.IsEnabled && (failed || JsonNode.DeepEquals(chosen, body))) chosen = null;
                Update();
            }
        }
    }

    public override void Leave()
    {
        if (debounce.IsEnabled)
        {
            debounce.Stop();
            Send();
        }
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

    /// <summary>A curve's (temperature, fan speed) points; a point that is not two numbers is skipped.</summary>
    internal static List<(double T, double D)> Points(JsonNode? curve) =>
        curve.Arr("points").Where(point => point.Num("0") is not null && point.Num("1") is not null)
            .Select(point => (point.Num("0")!.Value, point.Num("1")!.Value)).ToList();

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
                if (Points(curve) is not { Count: >= 2 } points) continue;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(Map(MinT, points[0].D), false);
                    foreach (var (t, d) in points) g.LineTo(Map(t, d));
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
