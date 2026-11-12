using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Port of preview/theme-settings.mjs and theme-settings.css: the Appearance section with a card for each dark and
/// light theme. Choosing a card saves the theme on this device ("ee.theme.v1") and applies it at once.
/// </summary>
public sealed class ThemeSettings : StackPanel
{
    public const string StorageKey = "ee.theme.v1";

    readonly TextBlock current = new() { FontSize = 12, LineHeight = 18 };
    readonly TextBlock error = new() { FontSize = 13, LineHeight = 19, MinHeight = 19, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, IsVisible = false };
    readonly Dictionary<string, Card> cards = new(StringComparer.Ordinal);
    readonly List<Grid> grids = [];
    bool active = true;

    public ThemeSettings()
    {
        Spacing = 12;
        current.Bind(TextBlock.ForegroundProperty, "muted");
        error.Bind(TextBlock.ForegroundProperty, "danger");
        Children.Add(SettingsKit.H2("Appearance", "text", 17));
        Children.Add(SettingsKit.P("Choose a theme for this device.", "muted"));
        Children.Add(current);
        foreach (var (light, label) in new[] { (false, "Dark themes"), (true, "Light themes") })
        {
            var group = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            group.Children.Add(new TextBlock { Text = label, FontSize = 14, FontWeight = FontWeight.SemiBold, Margin = new Thickness(2, 0, 2, 12) }.Bind(TextBlock.ForegroundProperty, "text"));
            var grid = new Grid { ColumnSpacing = 12, RowSpacing = 12 };
            grids.Add(grid);
            foreach (var theme in ThemeTokens.Themes.Where(item => item.Light == light))
            {
                var card = new Card(theme);
                card.Chosen += () => Choose(card);
                cards[theme.Id] = card;
                grid.Children.Add(card);
            }
            group.Children.Add(grid);
            Children.Add(group);
        }
        Children.Add(error);
        Look.ThemeChanged += OnThemeChanged;
        Layout(3);
        Paint();
    }

    void OnThemeChanged(string _)
    {
        if (!active) return;
        ClearError();
        Paint();
    }

    /// <summary>Three cards a row; two when the window is 1000px wide or less.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.PropertyChanged += OnTopLevelChanged;
            Layout(top.ClientSize.Width <= 1000 ? 2 : 3);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top) top.PropertyChanged -= OnTopLevelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    void OnTopLevelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TopLevel.ClientSizeProperty && sender is TopLevel top) Layout(top.ClientSize.Width <= 1000 ? 2 : 3);
    }

    void Layout(int columns)
    {
        foreach (var grid in grids)
        {
            if (grid.ColumnDefinitions.Count == columns) continue;
            grid.ColumnDefinitions.Clear();
            grid.RowDefinitions.Clear();
            for (var i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var rows = (grid.Children.Count + columns - 1) / columns;
            for (var i = 0; i < rows; i++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < grid.Children.Count; i++)
            {
                Grid.SetColumn(grid.Children[i], i % columns);
                Grid.SetRow(grid.Children[i], i / columns);
            }
        }
    }

    void ClearError()
    {
        error.IsVisible = false;
        error.Text = "";
    }

    void Choose(Card card)
    {
        if (!active) return;
        ClearError();
        try { SetTheme(card.Info.Id); }
        catch (Exception exception)
        {
            if (error.Parent is Panel parent) parent.Children.Remove(error);
            if (card.Content.RowDefinitions.Count < 5) card.Content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            card.Content.Children.Add(error);
            Grid.SetRow(error, 4);
            Grid.SetColumnSpan(error, 2);
            error.Text = exception.Message is { Length: > 0 } message ? message : "Could not save the theme on this device.";
            error.IsVisible = true;
        }
        Paint();
    }

    /// <summary>Port of setTheme: saves the choice first, then applies it.</summary>
    public static void SetTheme(string id)
    {
        if (ThemeTokens.Themes.All(theme => theme.Id != id)) throw new ArgumentException("Choose an available theme.");
        try { Preferences.SetItem(StorageKey, id); }
        catch (Exception) { throw new InvalidOperationException("Could not save the theme on this device. Try again."); }
        if (Application.Current is { } app) Look.Apply(app, id);
    }

    void Paint()
    {
        if (!active) return;
        var theme = ThemeTokens.Find(Look.CurrentTheme);
        current.Text = "Current theme: " + theme.Name;
        foreach (var (id, card) in cards) card.Selected = id == theme.Id;
    }

    /// <summary>Stops responding and disables the choices (the mount's cleanup).</summary>
    public void Close()
    {
        active = false;
        Look.ThemeChanged -= OnThemeChanged;
        foreach (var card in cards.Values) card.Radio.IsEnabled = false;
    }

    /// <summary>label.theme-option: the preview, name, radio button, description and Selected state.</summary>
    sealed class Card : Border
    {
        readonly TextBlock status = new() { FontSize = 12, LineHeight = 18, MinHeight = 18 };
        bool selected;

        public Card(ThemeInfo theme)
        {
            Info = theme;
            Padding = new Thickness(10);
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(9);
            Cursor = new Cursor(StandardCursorType.Hand);
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"),
                ColumnSpacing = 10, RowSpacing = 6, VerticalAlignment = VerticalAlignment.Top,
            };
            var preview = Preview(theme);
            Grid.SetColumnSpan(preview, 2);
            Content.Children.Add(preview);
            var title = new TextBlock { Text = theme.Name, FontSize = 14, LineHeight = 20.3, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
                .Bind(TextBlock.ForegroundProperty, "text");
            Grid.SetRow(title, 1);
            Content.Children.Add(title);
            // The template leaves 8px for a label after the circle; this radio has none, so it sits on the right edge.
            Radio = new RadioButton
            {
                GroupName = "ee-theme", MinHeight = 0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Content = null,
                Margin = new Thickness(0, 0, -8, 0),
            };
            Avalonia.Automation.AutomationProperties.SetName(Radio, theme.Name);
            Avalonia.Automation.AutomationProperties.SetHelpText(Radio, theme.Description);
            Radio.IsCheckedChanged += (_, _) =>
            {
                if (Radio.IsChecked == true && !selected) Chosen?.Invoke();
            };
            Grid.SetRow(Radio, 1);
            Grid.SetColumn(Radio, 1);
            Content.Children.Add(Radio);
            var detail = new TextBlock { Text = theme.Description, FontSize = 12, LineHeight = 17.4, TextWrapping = TextWrapping.Wrap }.Bind(TextBlock.ForegroundProperty, "muted");
            Grid.SetRow(detail, 2);
            Grid.SetColumnSpan(detail, 2);
            Content.Children.Add(detail);
            status.Bind(TextBlock.ForegroundProperty, "accent");
            Grid.SetRow(status, 3);
            Grid.SetColumnSpan(status, 2);
            Content.Children.Add(status);
            Child = Content;
            PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Left && !selected && Radio.IsEnabled) Chosen?.Invoke();
            };
            PropertyChanged += (_, e) =>
            {
                if (e.Property == IsPointerOverProperty) Paint();
            };
            Paint();
        }

        public ThemeInfo Info { get; }

        public Grid Content { get; }

        public RadioButton Radio { get; }

        public event Action? Chosen;

        public bool Selected
        {
            get => selected;
            set
            {
                selected = value;
                Radio.IsChecked = value;
                status.Text = value ? "Selected" : "";
                Paint();
            }
        }

        void Paint()
        {
            this.Bind(BackgroundProperty, selected ? "selection-soft" : IsPointerOver ? "raised" : "panel");
            this.Bind(BorderBrushProperty, selected ? "accent" : IsPointerOver ? "muted" : "line-strong");
        }

        /// <summary>span.theme-preview: the theme's background, a top band, a side bar, a panel and two accent lines.</summary>
        static Control Preview(ThemeInfo theme)
        {
            IBrush Swatch(int index) => new SolidColorBrush(index < theme.Swatches.Count ? theme.Swatches[index] : Colors.Gray);
            var accentWidth = 0.0;
            var first = new Border { Height = 5, CornerRadius = new CornerRadius(2), Background = Swatch(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(42, 30, 0, 0) };
            var second = new Border { Height = 5, CornerRadius = new CornerRadius(2), Background = Swatch(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(42, 42, 0, 0) };
            var panel = new Panel
            {
                Height = 76,
                Children =
                {
                    new Border { Height = 14, VerticalAlignment = VerticalAlignment.Top, Background = Swatch(1), CornerRadius = new CornerRadius(5, 5, 0, 0) },
                    new Border { Width = 19, Margin = new Thickness(8, 22, 0, 8), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), Background = Swatch(1) },
                    new Border { Margin = new Thickness(34, 22, 8, 8), CornerRadius = new CornerRadius(3), Background = Swatch(1) },
                    first, second,
                },
            };
            var frame = new Border { CornerRadius = new CornerRadius(5), Background = Swatch(0), ClipToBounds = true, Child = panel, Margin = new Thickness(0, 0, 0, 4), IsHitTestVisible = false };
            frame.SizeChanged += (_, e) =>
            {
                var width = Math.Round(e.NewSize.Width * 0.35, 2);
                if (Math.Abs(width - accentWidth) < 0.01) return;
                accentWidth = width;
                first.Width = second.Width = width;
            };
            return frame;
        }
    }
}
