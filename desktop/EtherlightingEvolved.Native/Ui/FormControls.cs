using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Theme;
using Path = Avalonia.Controls.Shapes.Path;

namespace EtherlightingEvolved.Native.Ui;

/// <summary>
/// The status notice (p.notice). Empty, it takes no space; with text, it is a rounded box, in the danger colors for an
/// error. The connection variant (.connection-notice) also shows the online and waiting tones and sits 16px below the
/// previous block, unless a block layout places it and collapses that margin itself.
/// </summary>
public sealed class Notice : Border
{
    readonly TextBlock text = new() { FontSize = 13, LineHeight = 20, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly bool connection, ownMargin;

    public Notice(bool connection = false, bool ownMargin = true)
    {
        this.connection = connection;
        this.ownMargin = ownMargin;
        CornerRadius = new CornerRadius(7);
        Child = text;
        Set();
    }

    public string Text => text.Text ?? "";

    public bool IsError { get; private set; }

    /// <summary>Shows a message; no arguments clears it (status()).</summary>
    public void Set(string message = "", bool error = false, string tone = "")
    {
        text.Text = message;
        IsError = error && message.Length > 0;
        AutomationProperties.SetName(this, message);
        if (message.Length == 0)
        {
            text.IsVisible = false;
            Padding = default;
            BorderThickness = default;
            Margin = default;
            Background = Brushes.Transparent;
            return;
        }
        text.IsVisible = true;
        Padding = new Thickness(14, 12);
        BorderThickness = new Thickness(1);
        Margin = connection && ownMargin ? new Thickness(0, 16, 0, 0) : default;
        this.Bind(BackgroundProperty, error ? "error-surface" : "notice-surface").Bind(BorderBrushProperty, error ? "error-border" : "line");
        text.Bind(TextBlock.ForegroundProperty, error ? "danger" : connection && tone == "online" ? "green" : connection && tone == "waiting" ? "amber" : "muted");
    }

    /// <summary>
    /// Text that wraps asks for the whole width, as a block's widest content does in the browser, so a dialog sized to
    /// its content widens to its maximum for a long message.
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (text.IsVisible && double.IsFinite(availableSize.Width) && text.DesiredSize.Height > text.LineHeight * 1.5) return size.WithWidth(availableSize.Width);
        return size;
    }
}

/// <summary>
/// The connection state line (p.connection-state and showConnectionStatus): a 5px dot before the label, both in the
/// tone's color ("online" green, "waiting" amber, "error" red, otherwise muted).
/// </summary>
public sealed class StateLine : TextBlock
{
    readonly Ellipse dot = new() { Width = 5, Height = 5, Margin = new Thickness(0, 0, 7, 0) };
    readonly Run label = new();
    bool painted;

    public StateLine(string text = "", string tone = "")
    {
        TextWrapping = TextWrapping.Wrap;
        FontSize = 13;
        LineHeight = 19.5;
        Inlines = [new InlineUIContainer(dot) { BaselineAlignment = BaselineAlignment.Center }, label];
        Set(text, tone);
    }

    public string Label => label.Text ?? "";

    public string Tone { get; private set; } = "";

    public void Set(string text, string tone)
    {
        if (label.Text != text) label.Text = text;
        // Live updates repeat the same tone many times a second; the colors change only when the tone does.
        if (Tone == tone && painted) return;
        Tone = tone;
        painted = true;
        var token = tone switch { "online" => "green", "waiting" => "amber", "error" => "danger", _ => "muted" };
        this.Bind(ForegroundProperty, token);
        dot.Bind(Shape.FillProperty, token);
    }

    public void Set((string Label, string Tone) status) => Set(status.Label, status.Tone);
}

/// <summary>
/// A details element: a summary that opens and closes the content below it with a click, Enter or Space. A text
/// summary shows the browser's disclosure triangle; a caller can build its own summary instead (.help-card).
/// </summary>
public sealed class Disclosure : StackPanel
{
    static readonly Geometry Closed = Geometry.Parse("M0,0 L7.5,4.5 L0,9 Z");
    static readonly Geometry Opened = Geometry.Parse("M0,0 L9,0 L4.5,7.5 Z");
    readonly Path? marker;
    bool open;

    public Disclosure(string text, params Control[] content)
    {
        marker = new Path { Width = 9, Height = 9, VerticalAlignment = VerticalAlignment.Top }.Bind(Shape.FillProperty, "muted");
        var title = new TextBlock { Text = text, FontSize = 13, LineHeight = 18.85, VerticalAlignment = VerticalAlignment.Top }.Bind(TextBlock.ForegroundProperty, "muted");
        Summary = new Border { HorizontalAlignment = HorizontalAlignment.Stretch, Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { marker, title } } };
        AutomationProperties.SetName(Summary, text);
        Attach(content);
    }

    public Disclosure(Border summary, Control content)
    {
        Summary = summary;
        Attach([content]);
    }

    public Border Summary { get; }

    /// <summary>The content shown when open, laid out as blocks.</summary>
    public StackPanel Body { get; } = new();

    public event Action? Toggled;

    public bool Open
    {
        get => open;
        set
        {
            if (open == value) return;
            open = value;
            Update();
            Toggled?.Invoke();
        }
    }

    void Attach(Control[] content)
    {
        Summary.Background ??= Brushes.Transparent;
        Summary.Focusable = true;
        Summary.Cursor = new Cursor(StandardCursorType.Hand);
        Summary.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(Summary).Properties.IsLeftButtonPressed) return;
            Summary.Focus();
            Open = !Open;
            e.Handled = true;
        };
        Summary.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            Open = !Open;
            e.Handled = true;
        };
        Children.Add(Summary);
        foreach (var item in content) Body.Children.Add(item);
        Children.Add(Body);
        Update();
    }

    void Update()
    {
        Body.IsVisible = open;
        if (marker is null) return;
        marker.Data = open ? Opened : Closed;
        marker.Margin = open ? new Thickness(0, 6, 5, 0) : new Thickness(0, 5, 5, 0);
    }
}

/// <summary>
/// A select element: options with values, labels and an optional disabled state. Setting a value that no option has
/// leaves nothing selected, as select.value does. Changed is raised only for the user's choices.
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

    /// <summary>Replaces the options (replaceChildren) and selects the given value, or the first option as a new select does.</summary>
    public void SetOptions(IEnumerable<(string Value, string Label, bool Disabled)> items, string? value = null)
    {
        updating = true;
        try
        {
            options.Clear();
            options.AddRange(items);
            Items.Clear();
            foreach (var (_, label, disabled) in options) Items.Add(new ComboBoxItem { Content = label, IsEnabled = !disabled });
            SelectedIndex = value is null ? (options.Count > 0 ? 0 : -1) : options.FindIndex(option => option.Value == value);
        }
        finally { updating = false; }
    }

    public void Add(string value, string label, bool disabled = false)
    {
        updating = true;
        try
        {
            options.Add((value, label, disabled));
            Items.Add(new ComboBoxItem { Content = label, IsEnabled = !disabled });
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
/// A secret field with a Show and Hide button (passwordField). The field and the button's accessible names name the
/// secret.
/// </summary>
public sealed class SecretField : StackPanel
{
    public SecretField(string label, TextBox input)
    {
        Input = input;
        Spacing = 7;
        input.PasswordChar = '•';
        input.RevealPassword = false;
        AutomationProperties.SetName(input, label);
        var title = new TextBlock { Text = label, FontSize = 13, LineHeight = 18, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
        Reveal = Ui.Button("Show", "quiet");
        void Name(bool visible) => AutomationProperties.SetName(Reveal, $"{(visible ? "Hide" : "Show")} {label.ToLowerInvariant()}");
        Name(false);
        Reveal.Click += (_, _) =>
        {
            var visible = !input.RevealPassword;
            input.RevealPassword = visible;
            Reveal.Content = visible ? "Hide" : "Show";
            Name(visible);
        };
        var row = new DockPanel();
        DockPanel.SetDock(Reveal, Dock.Right);
        Reveal.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(Reveal);
        row.Children.Add(input);
        Children.Add(title);
        Children.Add(row);
    }

    public TextBox Input { get; }

    public Button Reveal { get; }
}

/// <summary>
/// label.check-row: a check box followed by its 13px label. Only the box dims when disabled, as in the browser, and a
/// click on the label toggles the box.
/// </summary>
public sealed class CheckRow : StackPanel
{
    public CheckRow(string label, string colorToken = "muted", double minHeight = 28)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        MinHeight = minHeight;
        HorizontalAlignment = HorizontalAlignment.Left;
        Background = Brushes.Transparent;
        // The template leaves 8px for a label after the box; the label here is a separate text, 8px away.
        Box = new CheckBox { MinHeight = 0, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -8, 0) };
        AutomationProperties.SetName(Box, label);
        var text = new TextBlock { Text = label, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.ForegroundProperty, colorToken);
        Children.Add(Box);
        Children.Add(text);
        text.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && Box.IsEffectivelyEnabled) Box.IsChecked = Box.IsChecked != true;
        };
    }

    public CheckBox Box { get; }
}
