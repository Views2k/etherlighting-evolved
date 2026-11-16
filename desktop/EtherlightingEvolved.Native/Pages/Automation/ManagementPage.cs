using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages;

/// <summary>
/// The frame the Automation and Profiles pages share (secondary.mjs): the view header, a 250px list beside an editor
/// column of at most 940px at the left, the status notice, and the operation gate that runs one change at a time and
/// keeps keyboard focus on the same control, or its replacement, after the page redraws.
/// </summary>
internal abstract class ManagementPage : Page
{
    /// <summary>The browser's data-focus: a stable name for a control that the page draws again.</summary>
    public static readonly AttachedProperty<string?> FocusKeyProperty =
        AvaloniaProperty.RegisterAttached<ManagementPage, Control, string?>("FocusKey");

    protected readonly Grid Body = new() { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto") };
    public override bool FullBleed => true;
    protected readonly TextBlock Notice = U.Text("", wrap: true);
    /// <summary>p.notice: hidden while empty.</summary>
    protected readonly Border NoticeBox;
    protected readonly Func<string, string, JsonNode?, Task<JsonNode?>> Send;
    protected bool Busy { get; private set; }
    protected bool Active { get; private set; } = true;
    string? pendingFocus;
    bool started;

    protected ManagementPage(AppShell shell) : base(shell)
    {
        Send = Workspace.WorkspaceRequest();
        Styles.Add(PageStyles());
        Body.Margin = new Thickness(24);
        Notice.FontSize = 13;
        Notice.LineHeight = 20;
        NoticeBox = new Border { Child = Notice, Padding = new Thickness(14, 12), CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(1), Margin = new Thickness(0, 12, 0, 0), IsVisible = false };
        SetNoticeTone(false);
        Body.Children.CollectionChanged += (_, _) =>
        {
            for (int i = 0; i < Body.RowDefinitions.Count; i++)
            {
                var child = i < Body.Children.Count ? Body.Children[i] : null;
                Body.RowDefinitions[i].Height = child?.Classes.Contains("workspace-split") == true ? GridLength.Star : GridLength.Auto;
                if (child != null) Grid.SetRow(child, i);
            }
        };
        SizeChanged += (_, e) => Body.Margin = e.NewSize.Height < 450 && e.NewSize.Width >= 850 ? new Thickness(16, 8) : new Thickness(24);
        Content = Body;
    }

    /// <summary>Runs the first load once the page is shown (the renderer's initial reload).</summary>
    protected void Start(Func<Task> load)
    {
        Loaded += async (_, _) =>
        {
            if (started || !Active) return;
            started = true;
            await Run(load);
        };
    }

    /// <summary>
    /// The operation gate (operationGate): one action at a time with the page disabled meanwhile. Errors go to the
    /// notice. Afterwards focus returns to the control that had it, to its replacement with the same focus key or
    /// accessible name, or to the page heading; a focus key requested during the action takes precedence.
    /// </summary>
    protected async Task Run(Func<Task> action)
    {
        if (Busy || !Active) return;
        var memo = RememberFocus();
        Busy = true;
        Body.IsEnabled = false;
        try { await action(); }
        catch (Exception error) { if (Active) Tell(error.Message is { Length: > 0 } message ? message : "The change could not be completed. Try again.", true); }
        finally
        {
            Busy = false;
            Body.IsEnabled = true;
            if (pendingFocus is { } key) { pendingFocus = null; FocusLater(key); }
            else RestoreFocus(memo);
        }
    }

    /// <summary>The browser's button(): runs a synchronous change that redraws, then puts focus back.</summary>
    protected void Act(Action action)
    {
        var memo = RememberFocus();
        action();
        if (pendingFocus is { } key) { pendingFocus = null; FocusLater(key); }
        else RestoreFocus(memo);
    }

    /// <summary>
    /// Moves focus to the control with this focus key once the running Run or Act action has redrawn the page
    /// (drawEditor(focusKey)).
    /// </summary>
    protected void RequestFocus(string key) => pendingFocus = key;

    /// <summary>Moves focus to the control with this focus key after the next layout, outside Run and Act.</summary>
    protected void FocusLater(string key) => Dispatcher.UIThread.Post(() =>
    {
        if (!Active || Busy) return;
        if (Focusables().FirstOrDefault(control => control.GetValue(FocusKeyProperty) == key) is { } target) Focus(target);
    }, DispatcherPriority.Loaded);

    protected static T Key<T>(T control, string key) where T : Control
    {
        control.SetValue(FocusKeyProperty, key);
        return control;
    }

    sealed record FocusMemo(Control Original, string? Key, string? Label);

    FocusMemo? RememberFocus()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused || !this.IsVisualAncestorOf(focused)) return null;
        var owner = focused.GetSelfAndVisualAncestors().OfType<Control>().TakeWhile(control => control != this)
            .FirstOrDefault(control => control.GetValue(FocusKeyProperty) is not null) ?? focused;
        return new FocusMemo(owner, owner.GetValue(FocusKeyProperty), Label(owner));
    }

    static string? Label(Control control) =>
        AutomationProperties.GetName(control) is { Length: > 0 } name ? name : control is ContentControl { Content: string text } ? text : null;

    /// <summary>A replacement for a focus key that is gone; the Automation page offers the selected rule.</summary>
    protected virtual string? FallbackFocus(string key) => null;

    void RestoreFocus(FocusMemo? memo)
    {
        if (memo is null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Active || Busy) return;
            var now = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            bool lost = now is null || now is TopLevel || now == memo.Original || now.GetVisualRoot() is null
                || this.IsVisualAncestorOf(now) && now is not (Button or TextBox or ComboBox or ToggleButton or Slider);
            if (!lost) return;
            var controls = Focusables().ToList();
            var target = memo.Original.GetVisualRoot() is not null && memo.Original.IsEffectivelyEnabled && memo.Original.IsEffectivelyVisible ? memo.Original
                : memo.Key is { } key ? controls.FirstOrDefault(control => control.GetValue(FocusKeyProperty) == key)
                    ?? (FallbackFocus(key) is { } fallback ? controls.FirstOrDefault(control => control.GetValue(FocusKeyProperty) == fallback) : null)
                : memo.Label is { } label ? controls.FirstOrDefault(control => Label(control) == label) : null;
            if (target is null && Body.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Classes.Contains("h1") || text.Classes.Contains("h2")) is { } heading)
            {
                heading.Focusable = true;
                target = heading;
            }
            if (target is not null) Focus(target);
        }, DispatcherPriority.Loaded);
    }

    static void Focus(Control target)
    {
        target.Focus();
        target.BringIntoView();
    }

    IEnumerable<Control> Focusables() => Body.GetLogicalDescendants().OfType<Control>()
        .Where(control => control.Focusable && control.IsEffectivelyEnabled && control.IsEffectivelyVisible && control.GetVisualRoot() is not null);

    protected void Tell(string message, bool error = false)
    {
        if (!Active) return;
        Notice.Text = message;
        NoticeBox.IsVisible = message.Length > 0;
        SetNoticeTone(error);
        Shell.Status(message, error);
    }

    protected bool NoticeIs(string message) => NoticeBox.IsVisible && Notice.Text == message;

    void SetNoticeTone(bool error)
    {
        Notice.Classes.Set("error", error);
        Notice.Bind(TextBlock.ForegroundProperty, error ? "danger" : "muted");
        NoticeBox.Bind(Border.BackgroundProperty, error ? "error-surface" : "notice-surface");
        NoticeBox.Bind(Border.BorderBrushProperty, error ? "error-border" : "line");
    }

    protected Task<JsonNode?> Get(string path) => Send(path, "GET", null);

    protected Button ActionButton(string label, Func<Task> action, string? kind = "quiet") =>
        U.Button(label, kind, () => Run(action));

    protected Button PlainButton(string label, Action action, string? kind = "quiet") =>
        U.Button(label, kind, () => Act(action));

    protected static Control Header(string title, string subtitle, params Control[] actions)
    {
        var header = U.ViewHeader(title, subtitle);
        var row = U.Wrap(8, actions);
        header.Children.RemoveAt(1);
        header.RowDefinitions = new RowDefinitions("Auto,Auto");
        header.Children.Add(row);
        void Layout(double width)
        {
            bool compact = width > 0 && width < 770;
            header.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Auto;
            Grid.SetRow(row, compact ? 1 : 0);
            Grid.SetColumn(row, compact ? 0 : 1);
            row.Margin = compact ? new Thickness(0, 10, 0, 0) : default;
        }
        header.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        Layout(900);
        return header;
    }

    /// <summary>
    /// .secondary-split: the list (250px, 210px below 1040px) with a hairline on its right, a 20px gap, then the editor,
    /// which fills its column up to 940px and stays at the left edge. Below 710px the list sits above the editor.
    /// </summary>
    protected static Grid Split(Control list, Control editor, Thickness? padding = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("250,*"), RowDefinitions = new RowDefinitions("*,Auto") };
        grid.Classes.Add("workspace-split");
        if (editor is StackPanel)
            editor = new ScrollViewer { Content = editor, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        editor.MinHeight = 0;
        editor.Margin = padding ?? new Thickness(4, 0, 4, 0);
        var listView = new ScrollViewer { Content = list, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var rail = new Border { Child = listView }.Bind(Border.BorderBrushProperty, "line");
        var column = new EditorColumn { Child = editor, MinHeight = 0 };
        Grid.SetColumn(column, 1);
        grid.Children.Add(rail);
        grid.Children.Add(column);
        void Layout(double width)
        {
            bool compact = width > 0 && width < 710;
            grid.ColumnDefinitions[0].Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(width < 1040 ? 210 : 250);
            grid.ColumnDefinitions[1].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            rail.BorderThickness = compact ? new Thickness(0, 0, 0, 1) : new Thickness(0, 0, 1, 0);
            rail.Padding = compact ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 16, 0);
            grid.RowDefinitions[0].Height = compact ? GridLength.Auto : GridLength.Star;
            grid.RowDefinitions[1].Height = compact ? GridLength.Star : GridLength.Auto;
            listView.MaxHeight = compact ? Math.Clamp(grid.Bounds.Height * .3, 80, 220) : double.PositiveInfinity;
            Grid.SetColumn(column, compact ? 0 : 1);
            Grid.SetRow(column, compact ? 1 : 0);
            column.Margin = compact ? new Thickness(0, 16, 0, 0) : new Thickness(20, 0, 0, 0);
        }
        grid.SizeChanged += (_, e) => Layout(e.NewSize.Width);
        Layout(900);
        return grid;
    }

    protected static Grid Editor(Control content, Control actions, Control? header = null)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        if (header != null) { header.Margin = new Thickness(0, 0, 0, 8); grid.Children.Add(header); }
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var footer = new Border { Child = actions, Padding = new Thickness(0, 12), BorderThickness = new Thickness(0, 1, 0, 0) }.Bind(Border.BorderBrushProperty, "line");
        Grid.SetRow(scroll, 1); Grid.SetRow(footer, 2);
        grid.Children.Add(scroll); grid.Children.Add(footer);
        return grid;
    }

    /// <summary>.secondary-loading and .retry-panel: centered muted text (and a Retry button), at least 180px tall.</summary>
    protected Control LoadingPanel(string text, Func<Task>? retry = null)
    {
        var message = U.P(text, "muted");
        message.TextAlignment = TextAlignment.Center;
        var content = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(message);
        if (retry is not null)
        {
            var button = ActionButton("Retry", retry, null);
            button.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(button);
        }
        return new Border { MinHeight = 180, Padding = new Thickness(24), VerticalAlignment = VerticalAlignment.Top, Child = content };
    }

    /// <summary>A .secondary-list button: heading line, detail lines in muted text, soft selection with the pink title.</summary>
    protected static Button ListItem(Control heading, IEnumerable<Control> details, bool selected, Func<Task> click)
    {
        var content = U.Column(5, heading);
        foreach (var line in details) content.Children.Add(line);
        var button = U.Button("", "list-item", click);
        button.Classes.Set("current", selected);
        button.Content = content;
        return button;
    }

    protected static TextBlock ItemName(string? text, bool strong = false)
    {
        var name = U.Text(text, null, true);
        name.FontSize = 13;
        name.LineHeight = 18;
        name.FontWeight = strong ? FontWeight.Bold : FontWeight.Medium;
        return name;
    }

    protected static TextBlock ItemLine(string? text)
    {
        var line = U.Text(text, "muted", true);
        line.FontSize = 13;
        line.LineHeight = 18;
        return line;
    }

    /// <summary>h2.section-heading: 14px semibold in the accent color.</summary>
    protected static TextBlock SectionHeading(string text)
    {
        var heading = U.Text(text, "accent");
        heading.FontSize = 14;
        heading.FontWeight = FontWeight.SemiBold;
        return heading;
    }

    /// <summary>p.help-text: 13px muted text at 1.55 line height.</summary>
    protected static TextBlock HelpText(string text)
    {
        var help = U.Text(text, "muted", true);
        help.FontSize = 13;
        help.LineHeight = 20;
        return help;
    }

    protected static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    protected static string Title(string value) => value.Length == 0 ? "" : char.ToUpperInvariant(value[0]) + value[1..];
    protected static IEnumerable<string> Values(JsonNode? array) => array.Arr().Select(n => n?.ToString() ?? "");
    public override Task<bool> CanLeave() => Task.FromResult(!Busy);
    public override void Leave() => Active = false;

    /// <summary>Styles for the list items and tabs these pages draw (style.css:341-344, automation.css:1-2).</summary>
    static Styles PageStyles()
    {
        var styles = new Styles();
        void Add(Func<Selector?, Selector> selector, params (AvaloniaProperty Property, object? Value)[] setters)
        {
            var style = new Style(selector);
            foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
            styles.Add(style);
        }
        static Selector Presenter(Selector s) => s.Template().OfType<ContentPresenter>().Name("PART_ContentPresenter");
        Add(s => s.OfType<Button>().Class("list-item"), (Layoutable.MinHeightProperty, 0.0), (TemplatedControl.PaddingProperty, new Thickness(11, 12)),
            (TemplatedControl.CornerRadiusProperty, new CornerRadius(5)), (TemplatedControl.BorderThicknessProperty, new Thickness(0)),
            (Layoutable.HorizontalAlignmentProperty, HorizontalAlignment.Stretch), (ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        Add(s => Presenter(s.OfType<Button>().Class("list-item")), (ContentPresenter.BackgroundProperty, Brushes.Transparent),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Look.Res("text")));
        foreach (var state in new[] { ":pointerover", ":pressed" })
            Add(s => Presenter(s.OfType<Button>().Class("list-item").Class(state)), (ContentPresenter.BackgroundProperty, Look.Res("hover-soft")),
                (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Look.Res("text")));
        Add(s => Presenter(s.OfType<Button>().Class("list-item").Class("current")), (ContentPresenter.BackgroundProperty, Look.Res("selection-soft")),
            (ContentPresenter.ForegroundProperty, Look.Res("pink")));
        foreach (var state in new[] { ":pointerover", ":pressed" })
            Add(s => Presenter(s.OfType<Button>().Class("list-item").Class("current").Class(state)), (ContentPresenter.BackgroundProperty, Look.Res("selection-hover")),
                (ContentPresenter.ForegroundProperty, Look.Res("pink")));
        // .automation-tabs .selected (automation.css:2) is imported before .button.quiet (style.css:86) and has the same
        // specificity, so the browser draws the selected tab like the other one. The tabs stay plain quiet buttons here.
        // .scope-toolbar .button: 30px chips
        Add(s => s.OfType<Button>().Class("chip"), (Layoutable.MinHeightProperty, 30.0), (TemplatedControl.PaddingProperty, new Thickness(10, 5)));
        // .swatch-button: the color itself fills the button; the border turns to the accent on hover
        foreach (var state in new[] { "", ":pointerover", ":pressed" })
            Add(s => Presenter(state.Length == 0 ? s.OfType<Button>().Class("swatch-button") : s.OfType<Button>().Class("swatch-button").Class(state)),
                (ContentPresenter.BackgroundProperty, Brushes.Transparent), (ContentPresenter.BorderBrushProperty, Look.Res(state.Length == 0 ? "line-strong" : "accent")));
        return styles;
    }
}

/// <summary>The editor column: fills the space it is given up to a maximum width (940px) and stays at the left.</summary>
internal sealed class EditorColumn : Decorator
{
    public double Limit { get; init; } = 940;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return default;
        Child.Measure(availableSize.WithWidth(Math.Min(availableSize.Width, Limit)));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Min(finalSize.Width, Limit), finalSize.Height));
        return finalSize;
    }
}

/// <summary>
/// A wrapping row like display:flex with flex-wrap: children keep their size, wrap to a new line when the row is full,
/// and a child marked with <see cref="PushProperty"/> takes an equal share of its line's free space before it
/// (margin-left: auto).
/// </summary>
internal sealed class FlexRow : Panel
{
    public static readonly AttachedProperty<bool> PushProperty = AvaloniaProperty.RegisterAttached<FlexRow, Control, bool>("Push");

    public double Gap { get; init; } = 8;

    public double LineGap { get; init; } = 8;

    public static T Push<T>(T control) where T : Control
    {
        control.SetValue(PushProperty, true);
        return control;
    }

    List<List<Control>> Lines(double width)
    {
        var lines = new List<List<Control>>();
        List<Control>? line = null;
        double used = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            double size = child.DesiredSize.Width;
            if (line is null || line.Count > 0 && used + Gap + size > width + 0.5)
            {
                line = [];
                lines.Add(line);
                used = 0;
            }
            else if (line.Count > 0) used += Gap;
            line.Add(child);
            used += size;
        }
        return lines;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, availableSize.Height));
        var lines = Lines(availableSize.Width);
        double width = 0, height = 0;
        foreach (var line in lines)
        {
            width = Math.Max(width, line.Sum(child => child.DesiredSize.Width) + Gap * (line.Count - 1));
            height += line.Max(child => child.DesiredSize.Height);
        }
        height += LineGap * Math.Max(0, lines.Count - 1);
        return new Size(Math.Min(width, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var line in Lines(finalSize.Width))
        {
            double height = line.Max(child => child.DesiredSize.Height);
            double used = line.Sum(child => child.DesiredSize.Width) + Gap * (line.Count - 1);
            int pushes = line.Count(child => child.GetValue(PushProperty));
            double share = pushes > 0 ? Math.Max(0, finalSize.Width - used) / pushes : 0;
            double x = 0;
            for (int i = 0; i < line.Count; i++)
            {
                var child = line[i];
                if (i > 0) x += Gap;
                if (child.GetValue(PushProperty)) x += share;
                child.Arrange(new Rect(x, y + (height - child.DesiredSize.Height) / 2, child.DesiredSize.Width, child.DesiredSize.Height));
                x += child.DesiredSize.Width;
            }
            y += height + LineGap;
        }
        return finalSize;
    }
}

