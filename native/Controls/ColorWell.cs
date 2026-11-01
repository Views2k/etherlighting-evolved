using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace EtherlightingEvolved.Controls;

public sealed class ColorWell : UserControl
{
    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly TextBox[] _rgb = new TextBox[3];
    private readonly Slider _brightness;
    private readonly TextBlock _level;
    private readonly Grid _brightnessLine;
    private readonly Border _hueColor;
    private readonly Canvas _sv;
    private readonly Ellipse _cursor;
    private readonly Slider _hue;
    private readonly Popup _popup;
    private string _value = "#FFFFFF";
    private double _h, _s, _v = 1;
    private bool _updating;

    public event Action? Changed;
    public void ClosePicker() => _popup.IsOpen = false;
    public bool ShowBrightness
    {
        get => _brightnessLine.Visibility == Visibility.Visible;
        set => _brightnessLine.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    public string Hex
    {
        get => _value;
        set { if (TryColor(value, out var color)) SetColor(color, false); }
    }
    public double Brightness
    {
        get => _brightness.Value;
        set
        {
            _updating = true;
            _brightness.Value = Math.Clamp(value, 0, 100);
            _level.Text = $"{Math.Round(_brightness.Value)}%";
            _updating = false;
        }
    }

    public ColorWell()
    {
        MinWidth = 150;
        var body = new StackPanel();
        var line = new Grid();
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        _swatch = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(4), Width = 22, Height = 22 };
        var open = new Button { Content = _swatch, Padding = new Thickness(3), MinHeight = 32, Width = 32, ToolTip = "Choose color" };
        System.Windows.Automation.AutomationProperties.SetName(open, "Choose color");
        _hex = new TextBox { Text = _value, FontSize = 12, MinWidth = 80, MaxLength = 7 };
        System.Windows.Automation.AutomationProperties.SetName(_hex, "Hex color");
        _hex.LostKeyboardFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitHex(); e.Handled = true; } };
        Grid.SetColumn(_hex, 1);
        line.Children.Add(open); line.Children.Add(_hex); body.Children.Add(line);
        var brightnessLine = _brightnessLine = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        brightnessLine.ColumnDefinitions.Add(new ColumnDefinition());
        brightnessLine.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        _brightness = new Slider { Minimum = 0, Maximum = 100, Value = 100, SmallChange = 1, LargeChange = 10, ToolTip = "Color brightness", MinWidth = 65 };
        System.Windows.Automation.AutomationProperties.SetName(_brightness, "Color brightness");
        _level = new TextBlock { Text = "100%", FontSize = 11, Foreground = Brush("#9296A5"), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(_level, 1); brightnessLine.Children.Add(_brightness); brightnessLine.Children.Add(_level);
        _brightness.ValueChanged += (_, _) => { _level.Text = $"{Math.Round(_brightness.Value)}%"; if (!_updating) Changed?.Invoke(); };
        body.Children.Add(brightnessLine);
        Content = body;

        var picker = new StackPanel { Width = 236 };
        var svGrid = new Grid { Height = 154, ClipToBounds = true, Margin = new Thickness(0, 0, 0, 10) };
        _hueColor = new Border { Background = Brushes.Red };
        svGrid.Children.Add(_hueColor);
        svGrid.Children.Add(new Border { Background = new LinearGradientBrush(Colors.White, Colors.Transparent, 0) });
        svGrid.Children.Add(new Border { Background = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90) });
        _sv = new Canvas { Background = Brushes.Transparent, Focusable = true, Cursor = Cursors.Cross };
        _cursor = new Ellipse { Width = 12, Height = 12, Stroke = Brushes.White, StrokeThickness = 2, Fill = Brushes.Transparent, IsHitTestVisible = false,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = .8 } };
        _sv.Children.Add(_cursor);
        System.Windows.Automation.AutomationProperties.SetName(_sv, "Color saturation and brightness. Use arrow keys to adjust.");
        _sv.MouseLeftButtonDown += (_, e) => { _sv.Focus(); _sv.CaptureMouse(); ChoosePoint(e.GetPosition(_sv)); e.Handled = true; };
        _sv.MouseMove += (_, e) => { if (_sv.IsMouseCaptured) ChoosePoint(e.GetPosition(_sv)); };
        _sv.MouseLeftButtonUp += (_, _) => _sv.ReleaseMouseCapture();
        _sv.SizeChanged += (_, _) => PlaceCursor();
        _sv.KeyDown += (_, e) =>
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? .1 : .01;
            if (e.Key == Key.Left) _s -= step;
            else if (e.Key == Key.Right) _s += step;
            else if (e.Key == Key.Up) _v += step;
            else if (e.Key == Key.Down) _v -= step;
            else return;
            _s = Math.Clamp(_s, 0, 1); _v = Math.Clamp(_v, 0, 1);
            ApplyHsv(); e.Handled = true;
        };
        svGrid.Children.Add(_sv); picker.Children.Add(svGrid);
        var hueTrack = new Grid { Height = 24, Margin = new Thickness(0, 0, 0, 10) };
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, .5), EndPoint = new Point(1, .5) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(FromHsv(i * 60, 1, 1), i / 6d));
        hueTrack.Children.Add(new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = rainbow, Margin = new Thickness(6, 0, 6, 0) });
        _hue = new Slider { Minimum = 0, Maximum = 360, SmallChange = 1, LargeChange = 30, Background = Brushes.Transparent, ToolTip = "Hue" };
        _hue.SetResourceReference(StyleProperty, "HueSlider");
        hueTrack.Children.Add(_hue); picker.Children.Add(hueTrack);
        System.Windows.Automation.AutomationProperties.SetName(_hue, "Hue");
        _hue.ValueChanged += (_, _) => { if (!_updating) { _h = _hue.Value; ApplyHsv(); } };

        var rgbGrid = new Grid();
        for (int i = 0; i < 3; i++)
        {
            rgbGrid.ColumnDefinitions.Add(new ColumnDefinition());
            var channel = new StackPanel { Margin = new Thickness(i == 0 ? 0 : 7, 0, 0, 0) };
            channel.Children.Add(new TextBlock { Text = new[] { "Red", "Green", "Blue" }[i], FontSize = 11, Foreground = Brush("#9296A5"), Margin = new Thickness(0, 0, 0, 4) });
            var field = new TextBox { Text = "255", MaxLength = 3, Padding = new Thickness(7, 6, 7, 6) };
            _rgb[i] = field;
            System.Windows.Automation.AutomationProperties.SetName(field, new[] { "Red", "Green", "Blue" }[i]);
            field.LostKeyboardFocus += (_, _) => CommitRgb();
            field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitRgb(); e.Handled = true; } };
            channel.Children.Add(field); Grid.SetColumn(channel, i); rgbGrid.Children.Add(channel);
        }
        picker.Children.Add(rgbGrid);
        var shell = new Border
        {
            Background = Brush("#272932"), BorderBrush = Brush("#4A4D59"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(14), Margin = new Thickness(14, 12, 14, 20), Child = picker,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.Black, BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = .38 }
        };
        _popup = new Popup { Child = shell, PlacementTarget = open, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, HorizontalOffset = -14, VerticalOffset = -8 };
        open.Click += (_, _) => { _popup.IsOpen = !_popup.IsOpen; if (_popup.IsOpen) { PlaceCursor(); Motion.Enter(shell); } };
        shell.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { _popup.IsOpen = false; open.Focus(); e.Handled = true; } };
        Unloaded += (_, _) => _popup.IsOpen = false;
        IsEnabledChanged += (_, _) => { if (!IsEnabled) _popup.IsOpen = false; };
        SetColor(Colors.White, false);
    }

    private void CommitHex()
    {
        if (TryColor(_hex.Text, out var color)) SetColor(color, true);
        else { _hex.Text = _value; _hex.ToolTip = "Enter a six-digit hex color, such as #79AFFF."; }
    }
    private void CommitRgb()
    {
        if (byte.TryParse(_rgb[0].Text, out byte r) && byte.TryParse(_rgb[1].Text, out byte g) && byte.TryParse(_rgb[2].Text, out byte b)) SetColor(Color.FromRgb(r, g, b), true);
        else SetColor((Color)ColorConverter.ConvertFromString(_value), false);
    }
    private void ChoosePoint(Point p)
    {
        _s = Math.Clamp(p.X / Math.Max(1, _sv.ActualWidth), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, _sv.ActualHeight), 0, 1);
        ApplyHsv();
    }
    private void ApplyHsv() { Refresh(FromHsv(_h, _s, _v)); Changed?.Invoke(); }
    private void SetColor(Color color, bool notify)
    {
        double r = color.R / 255d, g = color.G / 255d, b = color.B / 255d;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d > 0) _h = (max == r ? ((g - b) / d + (g < b ? 6 : 0)) : max == g ? (b - r) / d + 2 : (r - g) / d + 4) * 60;
        _s = max == 0 ? 0 : d / max; _v = max;
        Refresh(color); if (notify) Changed?.Invoke();
    }
    private void Refresh(Color color)
    {
        _updating = true;
        _value = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        _hex.Text = _value; _hex.ToolTip = null;
        _swatch.Background = new SolidColorBrush(color);
        _rgb[0].Text = color.R.ToString(CultureInfo.InvariantCulture);
        _rgb[1].Text = color.G.ToString(CultureInfo.InvariantCulture);
        _rgb[2].Text = color.B.ToString(CultureInfo.InvariantCulture);
        _hue.Value = _h;
        _hueColor.Background = new SolidColorBrush(FromHsv(_h, 1, 1));
        PlaceCursor(); _updating = false;
    }
    private void PlaceCursor()
    {
        Canvas.SetLeft(_cursor, _s * _sv.ActualWidth - 6);
        Canvas.SetTop(_cursor, (1 - _v) * _sv.ActualHeight - 6);
    }
    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
    private static bool TryColor(string? value, out Color color)
    {
        string text = (value ?? "").Trim().TrimStart('#'); color = Colors.White;
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int number)) return false;
        color = Color.FromRgb((byte)(number >> 16), (byte)(number >> 8), (byte)number); return true;
    }
    private static Color FromHsv(double hue, double saturation, double value)
    {
        double h = (hue % 360 + 360) % 360 / 60, c = value * saturation, x = c * (1 - Math.Abs(h % 2 - 1)), m = value - c;
        (double r, double g, double b) = h < 1 ? (c, x, 0d) : h < 2 ? (x, c, 0d) : h < 3 ? (0d, c, x) : h < 4 ? (0d, x, c) : h < 5 ? (x, 0d, c) : (c, 0d, x);
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
