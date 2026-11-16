using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using EtherlightingEvolved.Native.Theme;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Markup helpers shared by the Settings sections, ported from the small builders at the top of preview/secondary.mjs
/// and preview/connections.mjs (node, field, button, section, frame, notice) and the matching rules in style.css.
/// </summary>
public static class SettingsKit
{
    /// <summary>1ch of the 13px workspace font, for the max-width: 64ch and 76ch text measures.</summary>
    public const double Ch = 6.99;

    /// <summary>A paragraph with the browser's 1.5 line height.</summary>
    public static TextBlock P(string text, string? token = null, double size = 13)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, LineHeight = Math.Round(size * 1.5, 2) };
        if (token is not null) block.Bind(TextBlock.ForegroundProperty, token);
        return block;
    }

    /// <summary>p.help-text: 13px muted text at 1.55 line height, 64ch wide at most, 5px below the previous item.</summary>
    public static TextBlock HelpText(string text = "")
    {
        var block = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 20.15, MaxWidth = 64 * Ch,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0),
        };
        return block.Bind(TextBlock.ForegroundProperty, "muted");
    }

    /// <summary>An h2 in the browser's sizes: 20px by default, colored by the token given.</summary>
    public static TextBlock H2(string text, string token = "text", double size = 20)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = FontWeight.SemiBold, LetterSpacing = size >= 20 ? -0.3 : 0, TextWrapping = TextWrapping.Wrap,
            LineHeight = Math.Round(size * 1.45, 2),
        };
        return block.Bind(TextBlock.ForegroundProperty, token);
    }

    /// <summary>The automation-section heading (.section-heading): 14px semibold in the accent color.</summary>
    public static TextBlock SectionHeading(string text) => H2(text, "accent", 14);

    /// <summary>
    /// section.automation-section: a column with 12px gaps under a top line, 18px below it. The first section of its
    /// parent has neither (.automation-section:first-child).
    /// </summary>
    public static (Border Frame, StackPanel Body) AutomationSection(bool first = false)
    {
        var body = new StackPanel { Spacing = 12 };
        var frame = new Border
        {
            BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, first ? 0 : 18, 0, 0), Child = body,
        }.Bind(Border.BorderBrushProperty, "line");
        return (frame, body);
    }

    /// <summary>.panel: a top line and 20px above and below the content, optionally no wider than maxWidth.</summary>
    public static Control Panel(Control child, bool first = false, double maxWidth = double.PositiveInfinity)
    {
        var panel = new Border { BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, first ? 4 : 20, 0, 20), Child = child }
            .Bind(Border.BorderBrushProperty, "line");
        return double.IsInfinity(maxWidth) ? panel : LeftMax(panel, maxWidth);
    }

    /// <summary>A block with max-width: it fills the available width up to the maximum and stays on the left.</summary>
    public static Grid LeftMax(Control child, double maxWidth)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(1, GridUnitType.Star) { MaxWidth = maxWidth }, new ColumnDefinition(GridLength.Auto) } };
        grid.Children.Add(child);
        return grid;
    }

    /// <summary>
    /// The browser workspace's button() helper: the button is disabled while its action runs and enabled again
    /// afterwards when <paramref name="enabled"/> allows it.
    /// </summary>
    public static Button ActionButton(string label, Func<Task> action, string? classes = null, Func<bool>? enabled = null)
    {
        var button = UiKit.Button(label, classes);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Click += async (_, _) =>
        {
            if (!button.IsEnabled) return;
            button.IsEnabled = false;
            try { await action(); }
            catch (Exception) { }
            finally { button.IsEnabled = enabled?.Invoke() ?? true; }
        };
        return button;
    }

    /// <summary>A plain button (connections.mjs button()); clicks are handled by the caller.</summary>
    public static Button Button(string label, string? classes = null)
    {
        var button = UiKit.Button(label, classes);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        return button;
    }

    public static void SetLabel(this Button button, string text)
    {
        if (button.Content as string != text) button.Content = text;
    }

    /// <summary>label.field: a muted label above its control, with optional faint help text (small).</summary>
    public static StackPanel Field(string label, Control control, string? help = null)
    {
        var field = new StackPanel { Spacing = 7 };
        field.Children.Add(new TextBlock { Text = label, FontSize = 13, LineHeight = 18, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted"));
        field.Children.Add(control);
        if (help is not null) field.Children.Add(Small(help));
        return field;
    }

    /// <summary>The faint small text inside a field.</summary>
    public static TextBlock Small(string text = "") =>
        new TextBlock { Text = text, FontSize = 13, LineHeight = 18.85, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "faint");

    /// <summary>
    /// header.view-header from frame(): Ui.ViewHeader with the browser's line heights (h1 at 1.45, p at 1.5).
    /// </summary>
    public static Grid ViewHeader(string title, string subtitle, params Control[] actions)
    {
        var header = UiKit.ViewHeader(title, subtitle, actions);
        if (header.Children[0] is StackPanel { Children: [TextBlock heading, TextBlock text] })
        {
            heading.LineHeight = 40.6;
            text.LineHeight = 19.5;
            text.FontSize = 13;
        }
        return header;
    }

    /// <summary>A text input as the browser workspace styles it; spell checking and capitals are off.</summary>
    public static TextBox Input(int maxLength = 0, string? placeholder = null)
    {
        var box = new TextBox { Watermark = placeholder, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (maxLength > 0) box.MaxLength = maxLength;
        return box;
    }

    /// <summary>div.connection-actions: a wrapping row with 10px gaps.</summary>
    public static WrapPanel Actions(params Control[] children)
    {
        var row = new WrapPanel { ItemSpacing = 10, LineSpacing = 10, Orientation = Orientation.Horizontal };
        foreach (var child in children) row.Children.Add(child);
        return row;
    }

    /// <summary>Styles for these pages that Look.cs does not cover.</summary>
    public static Styles PageStyles()
    {
        var styles = new Styles();
        void Add(Func<Selector?, Selector> selector, params (AvaloniaProperty Property, object? Value)[] setters)
        {
            var style = new Style(selector);
            foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
            styles.Add(style);
        }
        static Selector Presenter(Selector s) => s.Template().OfType<Avalonia.Controls.Presenters.ContentPresenter>().Name("PART_ContentPresenter");
        // .settings-nav .button: 44px rows, text on the left, no border or fill until selected.
        Add(s => s.OfType<Button>().Class("settings-nav"), (Layoutable.MinHeightProperty, 44.0), (ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Left),
            (Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch));
        Add(s => Presenter(s.OfType<Button>().Class("settings-nav").Class("selected")), (Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Look.Res("selection-soft")),
            (Avalonia.Controls.Presenters.ContentPresenter.BorderBrushProperty, Brushes.Transparent), (Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Look.Res("pink")));
        Add(s => Presenter(s.OfType<Button>().Class("settings-nav").Class("selected").Class(":pointerover")), (Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty, Look.Res("selection-soft")),
            (Avalonia.Controls.Presenters.ContentPresenter.ForegroundProperty, Look.Res("pink")));
        return styles;
    }
}

/// <summary>dl.settings-details: muted labels beside their values in a 0.7fr / 1.3fr grid.</summary>
public sealed class DetailsList : Grid
{
    public DetailsList()
    {
        ColumnDefinitions = new ColumnDefinitions { new(0.7, GridUnitType.Star) { MinWidth = 130 }, new(1.3, GridUnitType.Star) };
        ColumnSpacing = 20;
        RowSpacing = 13;
    }

    public bool IsEmpty => Children.Count == 0;

    /// <summary>Shows the rows, reusing the existing text so live readings change only what changed.</summary>
    public void Set(IEnumerable<(string Label, string Value)> rows, Func<string, string?>? tip = null)
    {
        var list = rows.ToList();
        while (RowDefinitions.Count > list.Count)
        {
            RowDefinitions.RemoveAt(RowDefinitions.Count - 1);
            Children.RemoveRange(Children.Count - 2, 2);
        }
        for (var index = 0; index < list.Count; index++)
        {
            var (label, value) = list[index];
            if (index >= RowDefinitions.Count)
            {
                RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var newTerm = new TextBlock { FontSize = 13, LineHeight = 18.85, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
                var newDetail = new TextBlock { FontSize = 13, LineHeight = 18.85, TextWrapping = TextWrapping.Wrap };
                SetRow(newTerm, index);
                SetRow(newDetail, index);
                SetColumn(newDetail, 1);
                Children.Add(newTerm);
                Children.Add(newDetail);
            }
            var term = (TextBlock)Children[index * 2];
            var detail = (TextBlock)Children[index * 2 + 1];
            if (term.Text != label)
            {
                term.Text = label;
                ToolTip.SetTip(term, tip?.Invoke(label));
            }
            if (detail.Text != value) detail.Text = value;
        }
    }
}

/// <summary>
/// Block layout: children stacked top to bottom with CSS vertical margins, where adjacent margins collapse to the
/// larger one and an empty child (no height) lets the margins around it collapse through it.
/// </summary>
public sealed class BlockStack : Panel
{
    public static readonly AttachedProperty<double> TopProperty = AvaloniaProperty.RegisterAttached<BlockStack, Control, double>("Top");
    public static readonly AttachedProperty<double> BottomProperty = AvaloniaProperty.RegisterAttached<BlockStack, Control, double>("Bottom");

    static BlockStack() => AffectsParentMeasure<BlockStack>(TopProperty, BottomProperty);

    public static T Margins<T>(T control, double top, double bottom = 0) where T : Control
    {
        control.SetValue(TopProperty, top);
        control.SetValue(BottomProperty, bottom);
        return control;
    }

    IEnumerable<(Control Child, double Y)> Place(double width, bool measure)
    {
        double y = 0, margin = 0;
        var started = false;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            if (measure) child.Measure(new Size(width, double.PositiveInfinity));
            var height = child.DesiredSize.Height;
            var top = child.GetValue(TopProperty);
            var bottom = child.GetValue(BottomProperty);
            if (height <= 0)
            {
                margin = Math.Max(margin, Math.Max(top, bottom));
                continue;
            }
            y += started ? Math.Max(margin, top) : top;
            yield return (child, y);
            y += height;
            margin = bottom;
            started = true;
        }
        Extent = y + margin;
    }

    double Extent { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        foreach (var (child, _) in Place(availableSize.Width, true).ToList()) width = Math.Max(width, child.DesiredSize.Width);
        return new Size(width, Extent);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (child, y) in Place(finalSize.Width, false).ToList())
        {
            var desired = child.DesiredSize;
            var x = child.HorizontalAlignment switch
            {
                HorizontalAlignment.Right => finalSize.Width - desired.Width,
                HorizontalAlignment.Center => (finalSize.Width - desired.Width) / 2,
                _ => 0,
            };
            // A stretched block fills the width up to its max-width and stays on the left, as CSS blocks do.
            var width = child.HorizontalAlignment == HorizontalAlignment.Stretch ? Math.Min(finalSize.Width, child.MaxWidth) : desired.Width;
            child.Arrange(new Rect(x, y, width, desired.Height));
        }
        return finalSize;
    }
}

/// <summary>Marks routed input as handled while a page or section is inert (the browser's inert attribute).</summary>
public static class Inert
{
    public static void Attach(Control host, Func<bool> inert)
    {
        void Block(object? sender, RoutedEventArgs e)
        {
            if (inert()) e.Handled = true;
        }
        host.AddHandler(InputElement.KeyDownEvent, Block, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerPressedEvent, Block, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.PointerReleasedEvent, Block, RoutingStrategies.Tunnel);
        host.AddHandler(InputElement.TextInputEvent, Block, RoutingStrategies.Tunnel);
    }
}
