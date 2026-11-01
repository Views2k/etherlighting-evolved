using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace EtherlightingEvolved.Controls;

public sealed class PortCanvas : UserControl
{
    private sealed class Channel
    {
        public required Button Button;
        public required Border Selection;
        public required SolidColorBrush Fill;
        public required SolidColorBrush Light;
        public required SolidColorBrush Ink;
        public required TextBlock Name;
        public required TextBlock Power;
        public required TextBlock Speed;
        public required Border SpeedDot;
        public required Grid Device;
        public required Border Connector;
        public required Path Poe;
        public required Path Plus;
        public required Path Uplink;
        public required Ellipse Alert;
        public required Path Check;
        public Color Preview = Parse("#232631");
        public Color LinkColor = Parse("#3B4047");
    }
    private static readonly Color OneGb = Parse("#A7D7F5"), TwoGb = Parse("#8FC5FF"), FiveGb = Parse("#3493F4"), TenGb = Parse("#3AAED1"), Disconnected = Parse("#3B4047"), Disabled = Parse("#252930");
    private readonly Dictionary<int, Channel> _channels = new();
    private readonly TextBlock _selection, _modeHint;
    private readonly ToggleButton _live, _speed;
    private bool _showSpeed;
    private int _anchor = 1;
    public HashSet<int> SelectedPorts { get; } = new(Enumerable.Range(1, 8));
    public event Action? SelectionChanged;

    public PortCanvas()
    {
        MinHeight = 246;
        var root = new Grid { Margin = new Thickness(22, 2, 22, 10) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tools = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 4) };
        _modeHint = new TextBlock { Text = "Live lighting", FontSize = 12, Foreground = Brush("#A5A9B8"), VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(_modeHint);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Background = Brush("#1B1D24"), HorizontalAlignment = HorizontalAlignment.Right };
        _live = ModeButton("Live lighting", true); _speed = ModeButton("Link speed", false);
        _live.Click += (_, _) => SetMode(false); _speed.Click += (_, _) => SetMode(true);
        modes.Children.Add(_live); modes.Children.Add(_speed); DockPanel.SetDock(modes, Dock.Right); tools.Children.Add(modes);
        root.Children.Add(tools);
        var bank = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 4) };
        for (int p = 1; p <= 10; p++)
        {
            if (p == 9) bank.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            bank.ColumnDefinitions.Add(new ColumnDefinition());
            var channel = CreateChannel(p); Grid.SetColumn(channel, p <= 8 ? p - 1 : p); bank.Children.Add(channel);
        }
        var divider = new Border { Width = 1, Background = Brush("#30333D"), Margin = new Thickness(0, 28, 0, 14) };
        Grid.SetColumn(divider, 8); bank.Children.Add(divider); Grid.SetRow(bank, 1); root.Children.Add(bank);
        var legend = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 3, 0, 6) };
        legend.Children.Add(new TextBlock { Text = "Link speed", Foreground = Brush("#9296A5"), FontSize = 11, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        foreach (var entry in new[] { ("1 GbE", OneGb), ("2.5 GbE", TwoGb), ("5 GbE", FiveGb), ("10 GbE", TenGb), ("Disconnected", Disconnected), ("Disabled", Disabled) })
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 0) };
            item.Children.Add(new Border { Width = 8, Height = 8, Background = new SolidColorBrush(entry.Item2), CornerRadius = new CornerRadius(1), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = entry.Item1, FontSize = 11, Foreground = Brush("#B2B6C3"), VerticalAlignment = VerticalAlignment.Center }); legend.Children.Add(item);
        }
        Grid.SetRow(legend, 2); root.Children.Add(legend);
        var footer = new DockPanel { LastChildFill = false };
        _selection = new TextBlock { FontSize = 11, Foreground = Brush("#A5A9B8"), VerticalAlignment = VerticalAlignment.Center }; footer.Children.Add(_selection);
        var help = new TextBlock { Text = "Ctrl-click to add · Shift-click for a range", FontSize = 10, Foreground = Brush("#797F91"), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(help, Dock.Right); footer.Children.Add(help); Grid.SetRow(footer, 3); root.Children.Add(footer);
        Content = root; PaintSelection();
    }
    private static ToggleButton ModeButton(string text, bool selected) => new()
    { Content = text, IsChecked = selected, MinHeight = 26, FontSize = 11, Padding = new Thickness(10, 4, 10, 4), ToolTip = text == "Live lighting" ? "Show each port's current lighting color" : "Show negotiated link speed; the strip below retains its lighting color" };
    private void SetMode(bool speed)
    {
        _showSpeed = speed; _speed.IsChecked = speed; _live.IsChecked = !speed;
        _modeHint.Text = speed ? "Lighting output remains beneath each port" : "Live lighting";
        foreach (var channel in _channels.Values) PaintOutput(channel);
    }

    private Button CreateChannel(int port)
    {
        bool sfp = port > 8;
        var stack = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        var power = new TextBlock { Text = "", Height = 16, FontSize = 11, Foreground = Brush("#97B7D8"), TextAlignment = TextAlignment.Center };
        var name = new TextBlock { Text = sfp ? $"SFP {port}" : $"Port {port}", Height = 18, FontSize = 11, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        stack.Children.Add(power); stack.Children.Add(name);
        var device = new Grid { Width = 72, Height = 58, Opacity = .25 };
        device.Children.Add(new Border { Background = Brush("#25282E"), CornerRadius = new CornerRadius(5) });
        device.Children.Add(new Path { Data = Geometry.Parse("M 13,23 L 59,23 Q 61,23 61,26 L 61,33 Q 61,36 58,36 L 14,36 Q 11,36 11,33 L 11,26 Q 11,23 13,23 Z"), Fill = Brush("#C5C9D0") });
        for (int i = 0; i < 7; i++) device.Children.Add(new Rectangle { Width = 3, Height = 3, Fill = Brush("#454B54"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(17 + i * 4, 29, 0, 0) });
        device.Children.Add(new Ellipse { Width = 2.5, Height = 2.5, Fill = Brush("#717985"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(53, 29, 0, 0) });
        stack.Children.Add(new Viewbox { Height = 40, MaxWidth = 72, Stretch = Stretch.Uniform, Child = device });
        var connector = new Border { Width = 1, Height = 8, Background = Brush("#5F6672"), Opacity = .25, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Center }; stack.Children.Add(connector);
        var fill = new SolidColorBrush(Parse("#232631")); var ink = new SolidColorBrush(Colors.White);
        var tile = new Grid { Width = 72, Height = 66 };
        tile.Children.Add(new Border { Background = fill, CornerRadius = new CornerRadius(5) });
        var hover = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(5), Opacity = 0, IsHitTestVisible = false };
        var selection = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(5), Opacity = 0, IsHitTestVisible = false };
        tile.Children.Add(hover); tile.Children.Add(selection);
        tile.Children.Add(new TextBlock { Text = port.ToString(CultureInfo.InvariantCulture), Foreground = ink, FontSize = 18, Margin = new Thickness(8, 0, 0, 5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom });
        var poe = new Path { Data = Geometry.Parse("M 37,12 L 25,31 L 34,31 L 30,44 L 43,25 L 35,25 Z"), Stroke = ink, StrokeThickness = 1.6, Fill = Brushes.Transparent, Visibility = Visibility.Collapsed };
        var plus = new Path { Stroke = ink, StrokeThickness = 1.4, Visibility = Visibility.Collapsed };
        var uplink = new Path { Data = Geometry.Parse("M 25,28 L 36,17 L 47,28"), Stroke = ink, StrokeThickness = 1.7, Visibility = Visibility.Collapsed };
        var alert = new Ellipse { Width = 7, Height = 7, Fill = Brush("#F08493"), Stroke = Brush("#191C24"), StrokeThickness = 1, Margin = new Thickness(0, 7, 7, 0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        var check = new Path { Data = Geometry.Parse("M 52,53 L 56,57 L 64,49"), Stroke = ink, StrokeThickness = 1.5, Opacity = 0 };
        tile.Children.Add(poe); tile.Children.Add(plus); tile.Children.Add(uplink); tile.Children.Add(alert); tile.Children.Add(check);
        stack.Children.Add(new Viewbox { Height = 52, MaxWidth = 72, Stretch = Stretch.Uniform, Child = tile });
        var light = new SolidColorBrush(Parse("#232631"));
        stack.Children.Add(new Border { Height = 3, MaxWidth = 56, Background = light, CornerRadius = new CornerRadius(1.5), Margin = new Thickness(1, 4, 1, 0), ToolTip = "Lighting output" });
        var speedLine = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
        var speedDot = new Border { Width = 5, Height = 5, CornerRadius = new CornerRadius(2.5), Background = new SolidColorBrush(Disconnected), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
        var speed = new TextBlock { Text = "—", FontSize = 10, Foreground = Brush("#A5A9B8"), TextWrapping = TextWrapping.NoWrap };
        speedLine.Children.Add(speedDot); speedLine.Children.Add(speed); stack.Children.Add(speedLine);
        var button = new Button { Content = stack, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center };
        var chrome = new FrameworkElementFactory(typeof(Border)); chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch); chrome.AppendChild(presenter);
        button.Template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };
        System.Windows.Automation.AutomationProperties.SetName(button, $"{(sfp ? "SFP" : "Port")} {port}");
        button.Click += (_, _) => Select(port); button.MouseEnter += (_, _) => Motion.SetFadeTo(hover, .1); button.MouseLeave += (_, _) => Motion.SetFadeTo(hover, 0);
        button.PreviewKeyDown += (_, e) =>
        {
            int? next = e.Key switch { Key.Left => Math.Max(1, port - 1), Key.Right => Math.Min(10, port + 1), Key.Home => sfp ? 9 : 1, Key.End => sfp ? 10 : 8, _ => null };
            if (next.HasValue) { _channels[next.Value].Button.Focus(); if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) Select(next.Value); e.Handled = true; }
        };
        _channels[port] = new Channel { Button = button, Selection = selection, Fill = fill, Light = light, Ink = ink, Name = name, Power = power, Speed = speed, SpeedDot = speedDot, Device = device, Connector = connector, Poe = poe, Plus = plus, Uplink = uplink, Alert = alert, Check = check };
        return button;
    }
    private void Select(int port)
    {
        var modifiers = Keyboard.Modifiers;
        if ((port > 8) != (_anchor > 8)) { SelectedPorts.Clear(); _anchor = port; }
        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            if (!modifiers.HasFlag(ModifierKeys.Control)) SelectedPorts.Clear();
            for (int p = Math.Min(port, _anchor); p <= Math.Max(port, _anchor); p++) SelectedPorts.Add(p);
        }
        else if (modifiers.HasFlag(ModifierKeys.Control)) { if (!SelectedPorts.Add(port)) SelectedPorts.Remove(port); _anchor = port; }
        else { SelectedPorts.Clear(); SelectedPorts.Add(port); _anchor = port; }
        PaintSelection(); SelectionChanged?.Invoke();
    }
    public void SetSelection(IEnumerable<int> ports)
    {
        int[] valid = ports.Where(p => p is >= 1 and <= 10).Distinct().ToArray(); SelectedPorts.Clear();
        if (valid.Length > 0) { bool sfp = valid[0] > 8; foreach (int p in valid.Where(p => (p > 8) == sfp)) SelectedPorts.Add(p); }
        _anchor = SelectedPorts.Count == 0 ? 1 : SelectedPorts.Min(); PaintSelection(); SelectionChanged?.Invoke();
    }
    private void PaintSelection()
    {
        foreach (var (port, channel) in _channels)
        {
            bool selected = SelectedPorts.Contains(port); Motion.SetFadeTo(channel.Selection, selected ? .06 : 0); Motion.SetFadeTo(channel.Check, selected ? .8 : 0);
            System.Windows.Automation.AutomationProperties.SetHelpText(channel.Button, selected ? "Selected" : "Not selected");
        }
        if (_selection != null) _selection.Text = SelectedPorts.Count switch { 0 => "Choose a port to edit", 1 => $"{(SelectedPorts.First() > 8 ? "SFP" : "Port")} {SelectedPorts.First()} selected", _ => $"{SelectedPorts.Count} ports selected" };
    }
    public void SetFrame(JsonArray ports, JsonArray? sfp)
    { foreach (var node in ports) Update(node, false); if (sfp != null) foreach (var node in sfp) Update(node, true); }

    private void Update(JsonNode? item, bool sfp)
    {
        if (item is not JsonObject obj || !int.TryParse(obj["port"]?.ToString(), out int port) || !_channels.TryGetValue(port, out var channel)) return;
        var link = obj["link"] as JsonObject; var facts = obj["facts"] as JsonObject; var poe = obj["poe"] as JsonObject;
        bool? online = ReadBool(sfp ? obj["link"] : link?["link_up"]) ?? ReadBool(facts?["link"]);
        bool disabled = ReadBool(link?["admin_up"]) == false;
        bool anomaly = ReadBool(facts?["anomaly"]) == true || ReadBool(obj["anomaly"]) == true;
        bool uplink = ReadBool(facts?["uplink"]) == true || ReadBool(link?["uplink"]) == true;
        double? mbps = Number(link?["speed_mbps"]) ?? Number(facts?["speed"]);
        string speedText = disabled ? "Disabled" : online == false ? "Offline" : online == null ? "—" : mbps >= 1000 ? $"{mbps / 1000:0.#} GbE" : mbps > 0 ? $"{mbps:0} Mbps" : "Online";
        channel.LinkColor = disabled ? Disabled : online != true ? Disconnected : mbps >= 10000 ? TenGb : mbps >= 5000 ? FiveGb : mbps >= 2500 ? TwoGb : mbps >= 1000 ? OneGb : Parse("#768C9E");
        channel.Speed.Text = speedText; channel.SpeedDot.Background = new SolidColorBrush(channel.LinkColor);
        channel.Device.Opacity = online == true ? 1 : .25; channel.Connector.Opacity = online == true ? 1 : .25;
        string? label = obj["name"]?.ToString() ?? obj["client_name"]?.ToString(); channel.Name.Text = string.IsNullOrWhiteSpace(label) ? sfp ? $"SFP {port}" : $"Port {port}" : label;
        string tier = poe?["tier"]?.ToString() ?? facts?["poe"]?.ToString() ?? "off";
        bool delivering = ReadBool(poe?["delivering"]) ?? (tier != "off" && tier.StartsWith("poe", StringComparison.OrdinalIgnoreCase));
        double? watts = Number(poe?["power_w"]) ?? Number(facts?["poe_power"]);
        channel.Power.Text = !sfp && delivering && watts.HasValue ? $"{watts.Value:0.##} W" : "";
        channel.Poe.Visibility = !sfp && delivering && !uplink ? Visibility.Visible : Visibility.Collapsed;
        int plusCount = tier.Count(c => c == '+');
        channel.Plus.Data = Geometry.Parse(plusCount switch
        {
            >= 3 => "M 48,13 L 48,19 M 45,16 L 51,16 M 48,23 L 48,29 M 45,26 L 51,26 M 48,33 L 48,39 M 45,36 L 51,36",
            2 => "M 48,16 L 48,22 M 45,19 L 51,19 M 48,28 L 48,34 M 45,31 L 51,31",
            _ => "M 48,22 L 48,28 M 45,25 L 51,25"
        });
        channel.Plus.Visibility = !sfp && delivering && !uplink && plusCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        channel.Uplink.Visibility = uplink ? Visibility.Visible : Visibility.Collapsed; channel.Alert.Visibility = anomaly ? Visibility.Visible : Visibility.Collapsed;
        channel.Preview = Parse(obj["preview"]?.ToString() ?? (sfp ? "#232631" : obj["color"]?.ToString() ?? "#232631")); PaintOutput(channel);
        string details = $"{channel.Name.Text} · {speedText}";
        if (delivering) details += $" · {tier.ToUpperInvariant()}" + (watts.HasValue ? $" · {watts.Value:0.##} W" : "");
        if (uplink) details += " · Uplink"; if (anomaly) details += " · Anomaly detected"; if (obj["override"] is JsonObject) details += " · Pinned color";
        channel.Button.ToolTip = details;
    }
    private void PaintOutput(Channel channel)
    {
        Color tile = _showSpeed ? channel.LinkColor : channel.Preview; Animate(channel.Fill, tile); Animate(channel.Light, channel.Preview);
        double luminance = (.2126 * tile.R + .7152 * tile.G + .0722 * tile.B) / 255;
        channel.Ink.Color = luminance > .55 ? Parse("#14212E") : Parse("#F4F5FA");
    }
    private static void Animate(SolidColorBrush brush, Color target)
    {
        if (brush.Color == target) return;
        Color before = brush.Color; brush.BeginAnimation(SolidColorBrush.ColorProperty, null); brush.Color = target;
        if (SystemParameters.ClientAreaAnimation) brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(before, target, TimeSpan.FromMilliseconds(85)) { FillBehavior = FillBehavior.Stop });
    }
    private static bool? ReadBool(JsonNode? value) => bool.TryParse(value?.ToString(), out bool result) ? result : null;
    private static double? Number(JsonNode? value) => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double result) && double.IsFinite(result) ? result : null;
    private static Color Parse(string text) { try { return (Color)ColorConverter.ConvertFromString(text); } catch { return Color.FromRgb(35, 38, 49); } }
    private static SolidColorBrush Brush(string color) => new(Parse(color));
}
