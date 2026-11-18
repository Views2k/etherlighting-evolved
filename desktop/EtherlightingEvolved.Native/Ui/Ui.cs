using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Ui;

/// <summary>
/// Small builders for the markup patterns the browser workspace repeats, named after its HTML and CSS classes.
/// Classes are space separated, as in HTML: Ui.Button("Apply changes", "primary").
/// </summary>
public static class Ui
{
    public static T Classes<T>(this T control, string? classes) where T : StyledElement
    {
        if (string.IsNullOrWhiteSpace(classes)) return control;
        foreach (var name in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) control.Classes.Add(name);
        return control;
    }

    public static TextBlock Text(string? text, string? classes = null, bool wrap = false) =>
        new TextBlock { Text = text ?? "", TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis }.Classes(classes);

    /// <summary>A paragraph: wrapping text at 1.5 line height.</summary>
    public static TextBlock P(string? text, string? classes = null) => Text(text, "p " + classes, wrap: true);

    public static TextBlock H1(string text) => Text(text, "h1");

    public static TextBlock H2(string text) => Text(text, "h2");

    public static TextBlock H3(string text) => Text(text, "h3");

    /// <summary>A button with text and an optional leading icon from the workspace icon set.</summary>
    public static Button Button(string text, string? classes = null, Action? click = null, string? icon = null, string? tip = null)
    {
        object content = icon is null ? text : new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 7,
            Children = { new Icon(icon, 17) { VerticalAlignment = VerticalAlignment.Center }, new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center } },
        };
        var button = new Button { Content = content }.Classes(classes);
        if (icon is not null) button.Classes.Add("with-icon");
        if (tip is not null) ToolTip.SetTip(button, tip);
        if (click is not null) button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A button whose handler is asynchronous; errors are left to the handler (use Shell.Run).</summary>
    public static Button Button(string text, string? classes, Func<Task> click, string? icon = null, string? tip = null)
    {
        var button = Button(text, classes, (Action?)null, icon, tip);
        button.Click += async (_, _) => await click();
        return button;
    }

    public static Button IconButton(string icon, string label, Action click)
    {
        var button = new Button { Content = new Icon(icon, 18), Classes = { "icon-button" } };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.Click += (_, _) => click();
        return button;
    }

    public static StackPanel Row(double gap = 10, params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = gap, VerticalAlignment = VerticalAlignment.Center };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    public static WrapPanel Wrap(double gap = 8, params Control[] children)
    {
        var panel = new WrapPanel { ItemSpacing = gap, LineSpacing = gap };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static StackPanel Column(double gap = 10, params Control[] children)
    {
        var column = new StackPanel { Spacing = gap };
        foreach (var child in children) column.Children.Add(child);
        return column;
    }

    /// <summary>1ch of the 13px workspace font, for the browser's text measures such as max-width: 64ch.</summary>
    public const double Ch = 6.99;

    /// <summary>The browser workspace's .field: a muted 13px label above its control, 7px apart, and optional help text.</summary>
    public static StackPanel Field(string label, Control control, string? hint = null)
    {
        var field = new StackPanel { Spacing = 7, Classes = { "field" } };
        field.Children.Add(new TextBlock { Text = label, FontSize = 13, LineHeight = 18, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted"));
        field.Children.Add(control);
        if (hint is not null) field.Children.Add(Small(hint));
        return field;
    }

    /// <summary>A &lt;small&gt; in a field: 13px faint text at 1.45 line height.</summary>
    public static TextBlock Small(string text = "") =>
        new TextBlock { Text = text, FontSize = 13, LineHeight = 18.85, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "faint");

    /// <summary>
    /// p.help-text: 13px muted text, 64ch wide at most, 5px below the previous block. Its 1.55 line height is rounded
    /// down to 20px because layout rounds each text block up to whole pixels.
    /// </summary>
    public static TextBlock HelpText(string text = "") =>
        new TextBlock
            {
                Text = text, FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap, MaxWidth = 64 * Ch, HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 5, 0, 0),
            }
            .Bind(TextBlock.ForegroundProperty, "muted");

    public static CheckBox Check(string label, bool value = false, Action<bool>? changed = null)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        if (changed is not null) box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        return box;
    }

    public static TextBox Input(string? value = null, string? placeholder = null, Action<string>? changed = null, int maxLength = 0)
    {
        var box = new TextBox { Text = value ?? "", Watermark = placeholder, MaxLength = maxLength };
        if (changed is not null) box.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty) changed(box.Text ?? "");
        };
        return box;
    }

    /// <summary>A select: options are (value, label) pairs; the change handler receives the value.</summary>
    public static ComboBox Select(IReadOnlyList<(string Value, string Label)> options, string? selected = null, Action<string>? changed = null)
    {
        var box = new ComboBox
        {
            ItemsSource = options.Select(option => option.Label).ToList(), HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = Math.Max(0, options.ToList().FindIndex(option => option.Value == selected)),
        };
        box.Tag = options;
        if (changed is not null)
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex >= 0 && box.SelectedIndex < options.Count) changed(options[box.SelectedIndex].Value);
            };
        return box;
    }

    /// <summary>The value of a select made by <see cref="Select"/>.</summary>
    public static string? Value(this ComboBox box) =>
        box.Tag is IReadOnlyList<(string Value, string Label)> options && box.SelectedIndex >= 0 && box.SelectedIndex < options.Count ? options[box.SelectedIndex].Value : null;

    public static Slider Range(double min, double max, double step, double value, Action<double>? changed = null)
    {
        var slider = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, TickFrequency = step, IsSnapToTickEnabled = step > 0,
            Value = Math.Clamp(value, min, max), VerticalAlignment = VerticalAlignment.Center,
        };
        if (changed is not null)
            slider.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) changed(slider.Value); };
        return slider;
    }

    /// <summary>A 1px divider in the line color.</summary>
    public static Border Divider(Thickness? margin = null) => new Border { Height = 1, Margin = margin ?? default }.Bind(Border.BackgroundProperty, "line");

    /// <summary>The browser workspace's .empty-state: centered heading and muted text.</summary>
    public static Control EmptyState(string heading, string text, Control? action = null)
    {
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520, Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = heading, Classes = { "h1" }, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = text, Classes = { "muted" }, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 20 });
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(action);
        }
        return panel;
    }

    /// <summary>The view header used by every secondary page: title, muted subtitle, and actions on the right.</summary>
    public static Grid ViewHeader(string title, string subtitle, params Control[] actions)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 42, Margin = new Thickness(0, 0, 0, 20) };
        var heading = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(H1(title));
        heading.Children.Add(Text(subtitle, "muted", wrap: true));
        grid.Children.Add(heading);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var action in actions) row.Children.Add(action);
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }

    public static void OnClick(this Button button, Action action) => button.Click += (_, _) => action();

    public static void Enable(this Control control, bool enabled) => control.IsEnabled = enabled;

    public static T Tip<T>(this T control, string? tip) where T : Control
    {
        ToolTip.SetTip(control, tip);
        return control;
    }

    public static IBrush Brush(string hex) => ThemeTokens.TryColor(hex, out var color) ? new SolidColorBrush(color) : Brushes.Transparent;
}
