using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Path = Avalonia.Controls.Shapes.Path;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// Builders for the markup the connection views share (preview/connections.mjs: input, field, button,
/// passwordField, notice), with the sizes and colors from the connection rules in preview/style.css.
/// </summary>
public static class ConnectionUi
{
    /// <summary>
    /// Line heights from style.css at 13px: labels 18px, body 1.45, paragraphs 1.5, help text 1.55. Layout rounds
    /// each text block up to whole pixels, so the fractional heights are rounded down here to keep the same totals.
    /// </summary>
    public const double LabelLine = 18, BodyLine = 18.85, ParagraphLine = 19.5, HelpLine = 20;

    /// <summary>A text input (input()). Spell checking and capitalization are off, as in the browser workspace.</summary>
    public static TextBox Input(int maxLength = 0, string? placeholder = null, bool password = false)
    {
        var box = new TextBox { MaxLength = maxLength, Watermark = placeholder, AcceptsReturn = false, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (password) box.PasswordChar = '•';
        return box;
    }

    /// <summary>The .field wrapper: a muted label, the control, and an optional faint help line (field()).</summary>
    public static StackPanel Field(string label, Control control, string? help = null)
    {
        var field = new StackPanel { Spacing = 7, Classes = { "field" } };
        field.Children.Add(FieldLabel(label));
        field.Children.Add(control);
        if (help is not null) field.Children.Add(Small(help));
        return field;
    }

    public static TextBlock FieldLabel(string label) =>
        new TextBlock { Text = label, FontSize = 13, LineHeight = LabelLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");

    /// <summary>A &lt;small&gt; inside a field: 13px in the faint color.</summary>
    public static TextBlock Small(string text = "") =>
        new TextBlock { Text = text, FontSize = 13, LineHeight = BodyLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "faint");

    /// <summary>The .help-text paragraph: 13px muted text at 1.55 line height, 5px below the previous block.</summary>
    public static TextBlock HelpText(string text = "") =>
        new TextBlock
            {
                Text = text, FontSize = 13, LineHeight = HelpLine, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), MaxWidth = 64 * 7.3,
                HorizontalAlignment = HorizontalAlignment.Left,
            }
            .Bind(TextBlock.ForegroundProperty, "muted");

    /// <summary>A workspace button (button()): "primary", "quiet" or the default style.</summary>
    public static Button Button(string text, string? classes = null) => UiKit.Button(text, classes);

    /// <summary>
    /// A secret field with a Show and Hide button (passwordField). The button's accessible name names the field.
    /// </summary>
    public static (StackPanel Field, Button Reveal) PasswordField(string label, TextBox control)
    {
        var wrapper = new StackPanel { Spacing = 7, Classes = { "field" } };
        var reveal = Button("Show", "quiet");
        void Name(bool visible) => Avalonia.Automation.AutomationProperties.SetName(reveal, $"{(visible ? "Hide" : "Show")} {label.ToLowerInvariant()}");
        Name(false);
        reveal.Click += (_, _) =>
        {
            var visible = !control.RevealPassword;
            control.RevealPassword = visible;
            reveal.Content = visible ? "Hide" : "Show";
            Name(visible);
        };
        var row = new DockPanel { Classes = { "connection-secret" } };
        DockPanel.SetDock(reveal, Dock.Right);
        reveal.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(reveal);
        row.Children.Add(control);
        Avalonia.Automation.AutomationProperties.SetName(control, label);
        wrapper.Children.Add(FieldLabel(label));
        wrapper.Children.Add(row);
        return (wrapper, reveal);
    }

    /// <summary>
    /// A check box and its label (label.check or .check-row). Disabling the box dims only the box, as in the
    /// browser; clicking the label toggles it.
    /// </summary>
    public static (StackPanel Row, CheckBox Box) CheckRow(string text, string colorToken, double minHeight)
    {
        var box = new CheckBox { MinHeight = minHeight, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent }.Bind(TextBlock.ForegroundProperty, colorToken);
        label.PointerPressed += (_, e) =>
        {
            if (!box.IsEffectivelyEnabled || !e.GetCurrentPoint(label).Properties.IsLeftButtonPressed) return;
            box.IsChecked = box.IsChecked != true;
            box.Focus();
            e.Handled = true;
        };
        Avalonia.Automation.AutomationProperties.SetName(box, text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, MinHeight = minHeight, HorizontalAlignment = HorizontalAlignment.Left, Children = { box, label } };
        return (row, box);
    }

    /// <summary>A left-aligned block that fills the width up to a maximum, as a block with max-width does.</summary>
    public static Grid LeftColumn(Control child, double maxWidth) =>
        new() { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = maxWidth } }, Children = { child } };

    /// <summary>The h2 used in connection headings: 20px semibold in the accent color.</summary>
    public static TextBlock Heading(string text) =>
        new TextBlock { Text = text, Classes = { "h2" }, LineHeight = 29, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "accent");

    public static Image Logo(double size)
    {
        var logo = new Image { Width = size, Height = size, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        try
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/logo.png"));
            logo.Source = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, (int)(size * 2), Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        }
        catch (Exception) { }
        return logo;
    }

    /// <summary>Opens an address in the default browser, like a link with target _blank.</summary>
    public static async Task OpenLink(Visual owner, string url)
    {
        try
        {
            if (TopLevel.GetTopLevel(owner)?.Launcher is { } launcher) await launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception) { }
    }
}

/// <summary>
/// The status notice (notice(): p.notice.connection-notice). Empty, it takes no space; with text, it is a rounded
/// box 16px below the previous block, red for errors, green for "online" and amber for "waiting".
/// </summary>
public sealed class Notice : Border
{
    readonly TextBlock text = new() { FontSize = 13, LineHeight = ConnectionUi.HelpLine, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public Notice()
    {
        CornerRadius = new CornerRadius(7);
        Child = text;
        Set();
    }

    public string Text => text.Text ?? "";

    public bool IsError { get; private set; }

    /// <summary>Shows a message; no arguments clears it (status()).</summary>
    public void Set(string text = "", bool error = false, string tone = "")
    {
        this.text.Text = text;
        IsError = error;
        var empty = text.Length == 0;
        this.text.IsVisible = !empty;
        Padding = empty ? default : new Thickness(14, 12);
        BorderThickness = empty ? default : new Thickness(1);
        Margin = empty ? default : new Thickness(0, 16, 0, 0);
        this.Bind(BackgroundProperty, error ? "error-surface" : "notice-surface");
        this.Bind(BorderBrushProperty, error ? "error-border" : "line");
        if (empty) Background = Brushes.Transparent;
        this.text.Bind(TextBlock.ForegroundProperty, error ? "danger" : tone == "online" ? "green" : tone == "waiting" ? "amber" : "muted");
        Avalonia.Automation.AutomationProperties.SetName(this, text);
    }

    /// <summary>
    /// Text that wraps asks for the whole width, as a block's widest content does in the browser, so a dialog
    /// sized to its content widens to its maximum for a long message.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (text.IsVisible && double.IsFinite(availableSize.Width) && text.DesiredSize.Height > text.LineHeight * 1.5) return size.WithWidth(availableSize.Width);
        return size;
    }
}

/// <summary>
/// The connection state line (p.connection-state and showConnectionStatus): a 5px dot and the label, both in the
/// tone's color ("online" green, "waiting" amber, "error" red, "is-disconnected" muted).
/// </summary>
public sealed class StateLine : DockPanel
{
    readonly Ellipse dot = new() { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 7, 0) };
    readonly TextBlock label = new() { FontSize = 13, LineHeight = ConnectionUi.ParagraphLine, TextWrapping = TextWrapping.Wrap };

    public StateLine(string text = "", string tone = "")
    {
        DockPanel.SetDock(dot, Dock.Left);
        Children.Add(dot);
        Children.Add(label);
        Show(text, tone);
    }

    public string Label => label.Text ?? "";

    public string Tone { get; private set; } = "";

    public void Show(string text, string tone)
    {
        if (label.Text != text) label.Text = text;
        Tone = tone;
        var token = tone switch { "online" => "green", "waiting" => "amber", "error" => "danger", _ => "muted" };
        label.Bind(TextBlock.ForegroundProperty, token);
        dot.Bind(Shape.FillProperty, token);
    }
}

/// <summary>
/// A &lt;details&gt; disclosure: a muted summary line with the browser's triangle marker that opens the content
/// below it.
/// </summary>
public sealed class Disclosure : StackPanel
{
    static readonly Geometry Closed = Geometry.Parse("M0,0 L7.5,4.5 L0,9 Z");
    static readonly Geometry Opened = Geometry.Parse("M0,0 L9,0 L4.5,7.5 Z");
    readonly Path marker = new() { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 5, 0) };
    readonly Border summary;
    bool open;

    public Disclosure(string text)
    {
        marker.Data = Closed;
        marker.Bind(Shape.FillProperty, "muted");
        var label = new TextBlock { Text = text, FontSize = 13, LineHeight = ConnectionUi.BodyLine, VerticalAlignment = VerticalAlignment.Top }.Bind(TextBlock.ForegroundProperty, "muted");
        summary = new Border
        {
            Background = Brushes.Transparent, Focusable = true, Cursor = new Cursor(StandardCursorType.Hand), HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { marker, label } },
        };
        Avalonia.Automation.AutomationProperties.SetName(summary, text);
        summary.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(summary).Properties.IsLeftButtonPressed) return;
            summary.Focus();
            IsOpen = !IsOpen;
            Toggled?.Invoke();
            e.Handled = true;
        };
        summary.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            IsOpen = !IsOpen;
            Toggled?.Invoke();
            e.Handled = true;
        };
        Children.Add(summary);
        Body.IsVisible = false;
        Children.Add(Body);
    }

    /// <summary>The content shown when open, laid out as blocks.</summary>
    public StackPanel Body { get; } = new();

    public event Action? Toggled;

    public bool IsOpen
    {
        get => open;
        set
        {
            open = value;
            marker.Data = value ? Opened : Closed;
            marker.Margin = value ? new Thickness(0, 6, 5, 0) : new Thickness(0, 5, 5, 0);
            Body.IsVisible = value;
        }
    }
}

/// <summary>
/// Blocks stacked as in normal page flow: the vertical margins of neighbors collapse to the larger one, and an
/// empty block's margins collapse through it. Hidden blocks take no space.
/// </summary>
public sealed class BlockStack : Panel
{
    public BlockStack(params Control[] children)
    {
        foreach (var child in children) Children.Add(child);
    }

    IEnumerable<(Control Child, double Top, double Height)> Layout(out double total)
    {
        var placed = new List<(Control, double, double)>();
        double y = 0, carried = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var margin = child.Margin;
            var height = Math.Max(0, child.DesiredSize.Height - margin.Top - margin.Bottom);
            if (height == 0)
            {
                carried = Math.Max(carried, Math.Max(margin.Top, margin.Bottom));
                placed.Add((child, y, 0));
                continue;
            }
            y += Math.Max(carried, margin.Top);
            placed.Add((child, y, height));
            y += height;
            carried = margin.Bottom;
        }
        total = y + carried;
        return placed;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            if (child.IsVisible) width = Math.Max(width, child.DesiredSize.Width);
        }
        Layout(out var total);
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, top, height) in Layout(out _))
        {
            var margin = child.Margin;
            child.Arrange(new Rect(0, top - margin.Top, finalSize.Width, height + margin.Top + margin.Bottom));
        }
        foreach (var child in Children)
            if (!child.IsVisible) child.Arrange(default);
        return finalSize;
    }
}

/// <summary>
/// A wrapping row with a fixed gap between items, as the flex rows .connection-actions (10px) and inline buttons
/// (no gap) do. Hidden items take no space and add no gap. Items are centered in their line.
/// </summary>
public sealed class FlowRow : Panel
{
    public FlowRow(double gap = 10, params Control[] children)
    {
        Gap = gap;
        foreach (var child in children) Children.Add(child);
    }

    public double Gap { get; set; }

    IEnumerable<List<Control>> Lines(double width)
    {
        var line = new List<Control>();
        double x = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var size = child.DesiredSize;
            if (line.Count > 0 && x + Gap + size.Width > width + 0.5)
            {
                yield return line;
                line = [];
                x = 0;
            }
            x += (line.Count > 0 ? Gap : 0) + size.Width;
            line.Add(child);
        }
        if (line.Count > 0) yield return line;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        double width = 0, height = 0;
        var first = true;
        foreach (var line in Lines(availableSize.Width))
        {
            width = Math.Max(width, line.Sum(child => child.DesiredSize.Width) + Gap * (line.Count - 1));
            height += (first ? 0 : Gap) + line.Max(child => child.DesiredSize.Height);
            first = false;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        var first = true;
        foreach (var line in Lines(finalSize.Width))
        {
            if (!first) y += Gap;
            first = false;
            var height = line.Max(child => child.DesiredSize.Height);
            double x = 0;
            foreach (var child in line)
            {
                var size = child.DesiredSize;
                child.Arrange(new Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Gap;
            }
            y += height;
        }
        foreach (var child in Children)
            if (!child.IsVisible) child.Arrange(default);
        return finalSize;
    }
}

/// <summary>
/// A &lt;select&gt;: options with values, labels and disabled entries. Setting a value that no option has selects
/// nothing, as in the browser. <see cref="Changed"/> is raised only for the user's choices.
/// </summary>
public sealed class SelectBox : ComboBox
{
    readonly List<(string Value, string Label, bool Disabled)> options = [];
    bool updating;

    public SelectBox()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        SelectionChanged += (_, _) =>
        {
            if (!updating) Changed?.Invoke();
        };
    }

    protected override Type StyleKeyOverride => typeof(ComboBox);

    public event Action? Changed;

    public IReadOnlyList<(string Value, string Label, bool Disabled)> Options => options;

    /// <summary>
    /// Replaces the options (replaceChildren). The first option is selected, as in a new select. The same options
    /// are left as they are, selection included, so a change handler can rebuild the list of the select that changed;
    /// every caller sets the value next.
    /// </summary>
    public void SetOptions(IEnumerable<(string Value, string Label, bool Disabled)> items)
    {
        var next = items.ToList();
        if (next.SequenceEqual(options)) return;
        updating = true;
        try
        {
            options.Clear();
            options.AddRange(next);
            Items.Clear();
            foreach (var (_, label, disabled) in options) Items.Add(new ComboBoxItem { Content = label, IsEnabled = !disabled });
            SelectedIndex = options.FindIndex(option => !option.Disabled);
        }
        finally { updating = false; }
    }

    public string Value
    {
        get => SelectedIndex >= 0 && SelectedIndex < options.Count ? options[SelectedIndex].Value : "";
        set
        {
            updating = true;
            try { SelectedIndex = options.FindIndex(option => option.Value == value); }
            finally { updating = false; }
        }
    }
}

/// <summary>
/// Port of confirmAction (preview/confirmation.mjs): a sheet with the title, a muted message, and Cancel and the
/// accept action. One confirmation per owner at a time; asking again with the same text returns the open one.
/// </summary>
public static class Confirmation
{
    sealed record Pending(string Title, string Message, string Accept, CancellationToken Signal, Task<bool> Promise, Dialog Dialog);

    static readonly ConditionalWeakTable<object, Pending> Open = new();

    public static Task<bool> ConfirmAction(Overlay overlay, object owner, string title, string message, string accept, CancellationToken signal = default)
    {
        if (signal.IsCancellationRequested) return Task.FromResult(false);
        // A closed dialog no longer counts, even before its result is handed back.
        if (Open.TryGetValue(owner, out var pending) && pending.Dialog.IsOpen)
            return pending.Title == title && pending.Message == message && pending.Accept == accept && pending.Signal == signal
                ? pending.Promise : Task.FromResult(false);
        var previousFocus = TopLevel.GetTopLevel(overlay)?.FocusManager?.GetFocusedElement() as Control;
        var heading = new TextBlock { Text = title, Classes = { "h2" }, FontSize = 18, LineHeight = 26, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var copy = new TextBlock { Text = message, FontSize = 13, LineHeight = ConnectionUi.ParagraphLine, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var actions = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14), Margin = new Thickness(0, 16, 0, 0), Child = buttons }
            .Bind(Border.BorderBrushProperty, "line");
        var sheet = new StackPanel { Spacing = 6, Children = { heading, copy, actions } };
        var dialog = overlay.Show(sheet, sheet: true);
        var cancel = ConnectionUi.Button("Cancel");
        var confirm = ConnectionUi.Button(accept, "primary");
        cancel.Click += (_, _) => dialog.Close("false");
        confirm.Click += (_, _) => dialog.Close("true");
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var registration = signal.Register(() => Dispatcher.UIThread.Post(() => dialog.Close("false")));
        Pending? entry = null;
        var promise = Finish();
        entry = new Pending(title, message, accept, signal, promise, dialog);
        Open.AddOrUpdate(owner, entry);
        return promise;

        async Task<bool> Finish()
        {
            var result = await dialog.Result;
            if (Open.TryGetValue(owner, out var latest) && ReferenceEquals(latest, entry)) Open.Remove(owner);
            registration.Dispose();
            if (owner is not Visual visual || Attached(visual))
                if (previousFocus is { IsEffectivelyEnabled: true } && Attached(previousFocus)) previousFocus.Focus();
            return result == "true";
        }
    }

    static bool Attached(Visual visual) => TopLevel.GetTopLevel(visual) is not null;
}
