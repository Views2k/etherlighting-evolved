using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace EtherlightingEvolved.Native.Theme;

/// <summary>
/// The workspace look from style.css as native styles. Class names follow the browser workspace's CSS classes
/// (button variants "primary", "quiet", "danger"; text "h1", "h2", "h3", "muted", "faint", "small"), so ported pages
/// keep the same names. Colors come from the active theme's tokens through dynamic resources.
/// </summary>
public static class Look
{
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI, Inter, $Default");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI, Inter, $Default");
    public static readonly Easing Ease = new SplineEasing(0.2, 0.7, 0.2, 1);
    public static string CurrentTheme { get; private set; } = ThemeTokens.DefaultTheme;

    public static event Action<string>? ThemeChanged;

    /// <summary>A dynamic theme resource, for use as a style setter value or a binding.</summary>
    public static DynamicResourceExtension Res(string token) => new(token);

    /// <summary>Binds a property to a theme token.</summary>
    public static T Bind<T>(this T control, AvaloniaProperty property, string token) where T : Control
    {
        control[!property] = new DynamicResourceExtension(token);
        return control;
    }

    public static void Apply(Application app, string theme)
    {
        var info = ThemeTokens.Find(theme);
        var tokens = ThemeTokens.Resolve(info.Id);
        var resources = app.Resources;
        foreach (var (key, value) in tokens) resources[key] = value;
        object Token(string name) => tokens.TryGetValue(name, out var value) ? value : Brushes.Transparent;
        Color ColorOf(string name) => tokens.TryGetValue(name + ".color", out var value) && value is Color color ? color : Colors.Transparent;

        // Fluent control resources, so every control state uses the workspace palette.
        var map = new Dictionary<string, string>
        {
            ["ButtonBackground"] = "button", ["ButtonBackgroundPointerOver"] = "button-hover", ["ButtonBackgroundPressed"] = "button-active",
            ["ButtonBackgroundDisabled"] = "button", ["ButtonForeground"] = "button-text", ["ButtonForegroundPointerOver"] = "button-text",
            ["ButtonForegroundPressed"] = "button-text", ["ButtonForegroundDisabled"] = "button-text", ["ButtonBorderBrush"] = "line",
            ["ButtonBorderBrushPointerOver"] = "button-hover-border", ["ButtonBorderBrushPressed"] = "line", ["ButtonBorderBrushDisabled"] = "line",
            ["TextControlBackground"] = "inset", ["TextControlBackgroundPointerOver"] = "inset", ["TextControlBackgroundFocused"] = "inset",
            ["TextControlBackgroundDisabled"] = "inset", ["TextControlForeground"] = "text", ["TextControlForegroundPointerOver"] = "text",
            ["TextControlForegroundFocused"] = "text", ["TextControlForegroundDisabled"] = "text", ["TextControlBorderBrush"] = "line-strong",
            ["TextControlBorderBrushPointerOver"] = "input-hover-border", ["TextControlBorderBrushFocused"] = "input-focus-border",
            ["TextControlBorderBrushDisabled"] = "line-strong", ["TextControlPlaceholderForeground"] = "placeholder",
            ["TextControlPlaceholderForegroundPointerOver"] = "placeholder", ["TextControlPlaceholderForegroundFocused"] = "placeholder",
            ["TextControlSelectionHighlightColor"] = "text-selection", ["TextControlButtonForeground"] = "muted",
            ["ComboBoxBackground"] = "inset", ["ComboBoxBackgroundPointerOver"] = "inset", ["ComboBoxBackgroundPressed"] = "inset",
            ["ComboBoxBackgroundDisabled"] = "inset", ["ComboBoxBackgroundFocused"] = "inset", ["ComboBoxBorderBrush"] = "line-strong",
            ["ComboBoxBorderBrushPointerOver"] = "input-hover-border", ["ComboBoxBorderBrushPressed"] = "input-focus-border",
            ["ComboBoxBorderBrushDisabled"] = "line-strong", ["ComboBoxForeground"] = "text", ["ComboBoxForegroundDisabled"] = "text",
            ["ComboBoxPlaceHolderForeground"] = "placeholder", ["ComboBoxDropDownGlyphForeground"] = "select-icon",
            ["ComboBoxDropDownGlyphForegroundDisabled"] = "select-icon", ["ComboBoxDropDownBackground"] = "option-surface",
            ["ComboBoxDropDownBorderBrush"] = "line-strong", ["ComboBoxItemForeground"] = "text", ["ComboBoxItemBackgroundPointerOver"] = "option-hover",
            ["ComboBoxItemBackgroundPressed"] = "option-hover", ["ComboBoxItemBackgroundSelected"] = "surface-active",
            ["ComboBoxItemBackgroundSelectedPointerOver"] = "option-hover", ["ComboBoxItemForegroundSelected"] = "option-selected",
            ["ComboBoxItemForegroundPointerOver"] = "text", ["ComboBoxItemForegroundSelectedPointerOver"] = "option-selected",
            ["CheckBoxForegroundUnchecked"] = "text", ["CheckBoxForegroundChecked"] = "text", ["CheckBoxForegroundIndeterminate"] = "text",
            ["CheckBoxForegroundUncheckedPointerOver"] = "text", ["CheckBoxForegroundCheckedPointerOver"] = "text",
            ["ToolTipBackground"] = "tooltip", ["ToolTipForeground"] = "text", ["ToolTipBorderBrush"] = "line-strong",
            ["FlyoutPresenterBackground"] = "dialog", ["FlyoutBorderThemeBrush"] = "line-strong", ["MenuFlyoutPresenterBackground"] = "dialog",
            ["MenuFlyoutPresenterBorderBrush"] = "line-strong", ["MenuFlyoutItemBackgroundPointerOver"] = "option-hover",
            ["MenuFlyoutItemForeground"] = "text", ["MenuFlyoutItemForegroundPointerOver"] = "text",
            ["ListBoxItemBackgroundPointerOver"] = "hover", ["ListBoxItemBackgroundSelected"] = "selection-soft",
            ["ListBoxItemBackgroundSelectedPointerOver"] = "selection-hover", ["ListBoxItemForeground"] = "text",
            ["ScrollBarThumbFill"] = "scrollbar", ["ScrollBarThumbFillPointerOver"] = "scrollbar-hover", ["ScrollBarThumbFillPressed"] = "scrollbar-hover",
            ["ScrollBarPanningThumbBackground"] = "scrollbar", ["ScrollBarBackground"] = "surface",
            ["ToggleSwitchFillOn"] = "check-fill", ["ToggleSwitchFillOnPointerOver"] = "check-fill", ["ToggleSwitchStrokeOff"] = "check-border",
            ["ToggleSwitchFillOff"] = "check-surface", ["ToggleSwitchKnobFillOff"] = "muted", ["ToggleSwitchKnobFillOn"] = "check-mark",
            ["ExpanderHeaderBackground"] = "surface", ["ExpanderContentBackground"] = "surface",
        };
        foreach (var (fluent, token) in map) resources[fluent] = Token(token);
        // The range progress runs start → middle (at 55% of the progress) → end, as the browser track gradient does.
        resources["range-progress-fill"] = new Avalonia.Media.Immutable.ImmutableLinearGradientBrush(
            [new(0, ColorOf("range-start")), new(0.55, ColorOf("range-middle")), new(1, ColorOf("range-end"))],
            startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative), endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
        resources["TextControlSelectionHighlightColor"] = Token("text-selection");
        resources["SystemAccentColor"] = ColorOf("accent");
        resources["SystemAccentColorLight1"] = ColorOf("accent");
        resources["SystemAccentColorLight2"] = ColorOf("accent");
        resources["SystemAccentColorDark1"] = ColorOf("accent");
        resources["SystemAccentColorDark2"] = ColorOf("accent");
        resources["SystemControlFocusVisualPrimaryBrush"] = Token("focus");
        resources["SystemControlFocusVisualSecondaryBrush"] = Brushes.Transparent;
        resources["TextControlBorderThemeThickness"] = new Thickness(1);
        resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
        resources["ControlContentThemeFontSize"] = 13.0;
        app.RequestedThemeVariant = info.Light ? ThemeVariant.Light : ThemeVariant.Dark;
        CurrentTheme = info.Id;
        ThemeChanged?.Invoke(info.Id);
    }

    public static Styles Styles()
    {
        var styles = new Styles();
        void Add(Func<Selector?, Selector> selector, params (AvaloniaProperty Property, object? Value)[] setters)
        {
            var style = new Style(selector);
            foreach (var (property, value) in setters) style.Setters.Add(new Setter(property, value));
            styles.Add(style);
        }
        static Selector Template(Selector s, string name) => s.Template().OfType<ContentPresenter>().Name(name);

        // Text
        Add(s => s.OfType<TextBlock>(), (TextBlock.FontFamilyProperty, Font));
        Add(s => s.OfType<TextBlock>().Class("h1"), (TextBlock.FontSizeProperty, 28.0), (TextBlock.FontWeightProperty, FontWeight.SemiBold),
            (TextBlock.LetterSpacingProperty, -0.65), (TextBlock.FontFamilyProperty, DisplayFont), (TextBlock.ForegroundProperty, Res("text")));
        Add(s => s.OfType<TextBlock>().Class("h2"), (TextBlock.FontSizeProperty, 20.0), (TextBlock.FontWeightProperty, FontWeight.SemiBold),
            (TextBlock.LetterSpacingProperty, -0.3), (TextBlock.FontFamilyProperty, DisplayFont), (TextBlock.ForegroundProperty, Res("text")));
        Add(s => s.OfType<TextBlock>().Class("h3"), (TextBlock.FontSizeProperty, 13.0), (TextBlock.FontWeightProperty, FontWeight.SemiBold));
        Add(s => s.OfType<TextBlock>().Class("muted"), (TextBlock.ForegroundProperty, Res("muted")));
        Add(s => s.OfType<TextBlock>().Class("faint"), (TextBlock.ForegroundProperty, Res("faint")));
        Add(s => s.OfType<TextBlock>().Class("accent"), (TextBlock.ForegroundProperty, Res("accent")));
        Add(s => s.OfType<TextBlock>().Class("danger"), (TextBlock.ForegroundProperty, Res("danger")));
        Add(s => s.OfType<TextBlock>().Class("small"), (TextBlock.FontSizeProperty, 12.0));
        Add(s => s.OfType<TextBlock>().Class("p"), (TextBlock.TextWrappingProperty, TextWrapping.Wrap), (TextBlock.LineHeightProperty, 19.5));

        // Buttons: default, primary, quiet, danger, icon-button
        Add(s => s.OfType<Button>(), (TemplatedControl.FontFamilyProperty, Font), (TemplatedControl.FontSizeProperty, 13.0),
            (TemplatedControl.FontWeightProperty, FontWeight.Medium), (Layoutable.MinHeightProperty, 34.0), (TemplatedControl.PaddingProperty, new Thickness(12, 7)),
            (TemplatedControl.CornerRadiusProperty, new CornerRadius(7)), (TemplatedControl.BorderThicknessProperty, new Thickness(1)),
            (Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center), (ContentControl.VerticalContentAlignmentProperty, VerticalAlignment.Center),
            (ContentControl.HorizontalContentAlignmentProperty, HorizontalAlignment.Center), (Visual.RenderTransformOriginProperty, RelativePoint.Center),
            (Animatable.TransitionsProperty, new Transitions { new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(90), Easing = Ease } }));
        Add(s => s.OfType<Button>().Class(":pressed"), (Visual.RenderTransformProperty, Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.985)")));
        Add(s => s.OfType<Button>().Class(":disabled"), (Visual.OpacityProperty, 0.42));
        Add(s => Template(s.OfType<Button>().Class("primary"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("primary-fill")),
            (ContentPresenter.BorderBrushProperty, Res("primary-border")), (ContentPresenter.ForegroundProperty, Res("on-primary")));
        Add(s => Template(s.OfType<Button>().Class("primary").Class(":pointerover"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("primary-hover")),
            (ContentPresenter.BorderBrushProperty, Res("primary-hover-border")), (ContentPresenter.ForegroundProperty, Res("on-primary")));
        Add(s => Template(s.OfType<Button>().Class("primary").Class(":pressed"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("primary-fill")),
            (ContentPresenter.ForegroundProperty, Res("on-primary")));
        Add(s => Template(s.OfType<Button>().Class("primary").Class(":disabled"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("primary-fill")),
            (ContentPresenter.ForegroundProperty, Res("on-primary")));
        foreach (var state in new[] { "", ":disabled" })
            Add(s => Template(state == "" ? s.OfType<Button>().Class("quiet") : s.OfType<Button>().Class("quiet").Class(state), "PART_ContentPresenter"),
                (ContentPresenter.BackgroundProperty, Brushes.Transparent), (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("muted")));
        Add(s => Template(s.OfType<Button>().Class("quiet").Class(":pointerover"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("hover")),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("text")));
        Add(s => Template(s.OfType<Button>().Class("quiet").Class(":pressed"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("hover-strong")),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("text")));
        Add(s => Template(s.OfType<Button>().Class("danger"), "PART_ContentPresenter"), (ContentPresenter.ForegroundProperty, Res("danger")));
        Add(s => Template(s.OfType<Button>().Class("danger").Class(":pointerover"), "PART_ContentPresenter"), (ContentPresenter.ForegroundProperty, Res("danger")));
        Add(s => s.OfType<Button>().Class("icon-button"), (Layoutable.WidthProperty, 30.0), (Layoutable.HeightProperty, 30.0), (Layoutable.MinHeightProperty, 0.0),
            (TemplatedControl.PaddingProperty, new Thickness(0)), (TemplatedControl.CornerRadiusProperty, new CornerRadius(5)));
        Add(s => Template(s.OfType<Button>().Class("icon-button"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Brushes.Transparent),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("muted")));
        Add(s => Template(s.OfType<Button>().Class("icon-button").Class(":pointerover"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("hover-strong")),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("text")));
        // Selected segment or tab ("selected" class or aria-pressed in the browser workspace)
        Add(s => Template(s.OfType<Button>().Class("selected"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("selection")),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("on-selection")));
        Add(s => Template(s.OfType<Button>().Class("selected").Class(":pointerover"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("selection")),
            (ContentPresenter.BorderBrushProperty, Brushes.Transparent), (ContentPresenter.ForegroundProperty, Res("on-selection")));

        // Title bar navigation (.navigation button) and window actions (Help, Settings)
        foreach (var kind in new[] { "nav", "window-action" })
        {
            Add(s => s.OfType<Button>().Class(kind), (Layoutable.MinHeightProperty, 34.0), (TemplatedControl.PaddingProperty, kind == "nav" ? new Thickness(14, 6) : new Thickness(12, 7)),
                (TemplatedControl.CornerRadiusProperty, new CornerRadius(kind == "nav" ? 6 : 7)), (TemplatedControl.FontWeightProperty, FontWeight.Normal),
                (TemplatedControl.BorderThicknessProperty, new Thickness(0)));
            Add(s => Template(s.OfType<Button>().Class(kind), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Brushes.Transparent),
                (ContentPresenter.ForegroundProperty, Res("muted")));
            Add(s => Template(s.OfType<Button>().Class(kind).Class(":pointerover"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("hover")),
                (ContentPresenter.ForegroundProperty, Res("text")));
            Add(s => Template(s.OfType<Button>().Class(kind).Class(":pressed"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("hover")),
                (ContentPresenter.ForegroundProperty, Res("text")));
            Add(s => Template(s.OfType<Button>().Class(kind).Class(":disabled"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Brushes.Transparent),
                (ContentPresenter.ForegroundProperty, Res("muted")));
            Add(s => Template(s.OfType<Button>().Class(kind).Class("current"), "PART_ContentPresenter"), (ContentPresenter.BackgroundProperty, Res("selection-soft")),
                (ContentPresenter.ForegroundProperty, Res(kind == "nav" ? "text" : "pink")));
            Add(s => Template(s.OfType<Button>().Class(kind).Class("current").Class(":pointerover"), "PART_ContentPresenter"),
                (ContentPresenter.BackgroundProperty, Res(kind == "nav" ? "selection-hover" : "selection-soft")), (ContentPresenter.ForegroundProperty, Res(kind == "nav" ? "text" : "pink")));
            Add(s => s.OfType<Button>().Class(kind).Descendant().OfType<Ui.Icon>(), (Ui.Icon.ForegroundProperty, Res("faint")));
            Add(s => s.OfType<Button>().Class(kind).Class(":pointerover").Descendant().OfType<Ui.Icon>(), (Ui.Icon.ForegroundProperty, Res("pink")));
            Add(s => s.OfType<Button>().Class(kind).Class("current").Descendant().OfType<Ui.Icon>(), (Ui.Icon.ForegroundProperty, Res("pink")));
        }
        Add(s => s.OfType<Button>().Class("nav").Class(":pressed"), (Visual.RenderTransformProperty, Avalonia.Media.Transformation.TransformOperations.Parse("scale(0.97)")));
        // Icons inside ordinary buttons take the button's text color, like currentColor.
        Add(s => s.OfType<Button>().Class("with-icon").Descendant().OfType<Ui.Icon>(),
            (Ui.Icon.ForegroundProperty, new Avalonia.Data.Binding { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(ContentPresenter) }, Path = "Foreground" }));

        // Inputs
        Add(s => s.OfType<TextBox>(), (TemplatedControl.FontFamilyProperty, Font), (TemplatedControl.FontSizeProperty, 13.0), (Layoutable.MinHeightProperty, 34.0),
            (TemplatedControl.PaddingProperty, new Thickness(10, 7)), (TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
            (TemplatedControl.BorderThicknessProperty, new Thickness(1)), (TextBox.SelectionBrushProperty, Res("text-selection")),
            (TextBox.SelectionForegroundBrushProperty, Res("on-text-selection")), (TextBox.CaretBrushProperty, Res("text")),
            (Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center), (TextBox.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        Add(s => s.OfType<TextBox>().Class("invalid"), (TemplatedControl.BorderBrushProperty, Res("input-error-border")));
        Add(s => s.OfType<TextBox>().Class(":disabled"), (Visual.OpacityProperty, 0.42));
        Add(s => s.OfType<ComboBox>(), (TemplatedControl.FontFamilyProperty, Font), (TemplatedControl.FontSizeProperty, 13.0), (Layoutable.MinHeightProperty, 34.0),
            (TemplatedControl.PaddingProperty, new Thickness(10, 7, 8, 7)), (TemplatedControl.CornerRadiusProperty, new CornerRadius(7)),
            (TemplatedControl.BorderThicknessProperty, new Thickness(1)), (Layoutable.VerticalAlignmentProperty, VerticalAlignment.Center));
        Add(s => s.OfType<ComboBox>().Class(":disabled"), (Visual.OpacityProperty, 0.42));
        Add(s => s.OfType<ComboBoxItem>(), (TemplatedControl.FontSizeProperty, 13.0), (TemplatedControl.PaddingProperty, new Thickness(8)),
            (TemplatedControl.CornerRadiusProperty, new CornerRadius(5)));

        // Check boxes and radio buttons: 15px boxes with the workspace check fill
        Add(s => s.OfType<CheckBox>(), (TemplatedControl.TemplateProperty, CheckTemplate(false)), (TemplatedControl.FontSizeProperty, 13.0),
            (Layoutable.MinHeightProperty, 32.0), (TemplatedControl.ForegroundProperty, Res("text")), (TemplatedControl.BackgroundProperty, Brushes.Transparent));
        Add(s => s.OfType<RadioButton>(), (TemplatedControl.TemplateProperty, CheckTemplate(true)), (TemplatedControl.FontSizeProperty, 13.0),
            (Layoutable.MinHeightProperty, 32.0), (TemplatedControl.ForegroundProperty, Res("text")), (TemplatedControl.BackgroundProperty, Brushes.Transparent));
        Add(s => s.Is<ToggleButton>().Class(":disabled"), (Visual.OpacityProperty, 0.42));

        // Range: 3px track with the gradient progress and a 12px thumb
        Add(s => s.OfType<Slider>(), (TemplatedControl.TemplateProperty, SliderTemplate()), (Layoutable.MinWidthProperty, 70.0), (Layoutable.HeightProperty, 22.0));
        Add(s => s.OfType<Slider>().Class(":disabled"), (Visual.OpacityProperty, 0.42));

        // Scroll bars: 8px rounded thumbs on a clear track
        Add(s => s.OfType<ScrollBar>(), (TemplatedControl.BackgroundProperty, Brushes.Transparent));
        Add(s => s.OfType<ToolTip>(), (TemplatedControl.FontSizeProperty, 12.0), (TemplatedControl.CornerRadiusProperty, new CornerRadius(6)),
            (TemplatedControl.PaddingProperty, new Thickness(8, 5)));
        return styles;
    }

    static IControlTemplate CheckTemplate(bool radio) => new FuncControlTemplate<ToggleButton>((control, scope) =>
    {
        var box = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(radio ? 7.5 : 4), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Control mark = radio
            ? new Ellipse { Width = 5, Height = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            : new ShapePath
            {
                Data = Geometry.Parse("M3.5,7.5 L6.2,10 L11.5,4.8"), StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-0.5, -0.5, 0, 0),
            };
        var dash = new Border { Width = 8, Height = 1.5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        box.Child = new Panel { Children = { mark, dash } };
        void Update()
        {
            var on = control.IsChecked == true;
            var mixed = control.IsChecked is null && !radio;
            box[!Border.BackgroundProperty] = Res(on || mixed ? "check-fill" : "check-surface");
            box[!Border.BorderBrushProperty] = Res(on || mixed ? "check-fill-border" : control.IsPointerOver && control.IsEnabled ? "check-hover-border" : "check-border");
            mark.IsVisible = on;
            dash.IsVisible = mixed;
        }
        if (mark is ShapePath path) path[!Shape.StrokeProperty] = Res("check-mark");
        else mark[!Shape.FillProperty] = Res("check-mark");
        dash[!Border.BackgroundProperty] = Res("check-mark");
        control.PropertyChanged += (_, e) =>
        {
            if (e.Property == ToggleButton.IsCheckedProperty || e.Property == InputElement.IsPointerOverProperty || e.Property == InputElement.IsEnabledProperty) Update();
        };
        Update();
        var content = new ContentPresenter
        {
            Name = "PART_ContentPresenter", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            [!ContentPresenter.ContentProperty] = control[!ContentControl.ContentProperty],
            [!ContentPresenter.ContentTemplateProperty] = control[!ContentControl.ContentTemplateProperty],
            [!ContentPresenter.ForegroundProperty] = control[!TemplatedControl.ForegroundProperty],
        };
        content.RegisterInNameScope(scope);
        var row = new DockPanel { Background = Brushes.Transparent, Children = { box, content } };
        DockPanel.SetDock(box, Dock.Left);
        return row;
    });

    static IControlTemplate SliderTemplate() => new FuncControlTemplate<Slider>((slider, scope) =>
    {
        var progress = new RepeatButton { Height = 3, CornerRadius = new CornerRadius(2), Focusable = false, Padding = default, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        progress.Template = new FuncControlTemplate<RepeatButton>((button, _) => new Border
        {
            CornerRadius = new CornerRadius(2), Height = 3,
            [!Border.BackgroundProperty] = Res("range-progress-fill"),
        });
        var rest = new RepeatButton { Height = 3, Focusable = false, Padding = default, MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
        rest.Template = new FuncControlTemplate<RepeatButton>((button, _) => new Border
        {
            CornerRadius = new CornerRadius(2), Height = 3,
            [!Border.BackgroundProperty] = Res("range-track"),
        });
        var knob = new Border
        {
            Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 1 3 0 #55000000"), RenderTransformOrigin = RelativePoint.Center,
            [!Border.BorderBrushProperty] = Res("range-thumb-border"), [!Border.BackgroundProperty] = Res("range-thumb"),
        };
        var thumb = new Thumb { Template = new FuncControlTemplate<Thumb>((_, _) => knob), VerticalAlignment = VerticalAlignment.Center };
        var dragging = false;
        void Paint()
        {
            knob[!Border.BackgroundProperty] = Res(dragging ? "range-thumb-active" : thumb.IsPointerOver ? "range-thumb-hover" : "range-thumb");
            knob.RenderTransform = dragging ? new ScaleTransform(1.12, 1.12) : null;
        }
        thumb.DragStarted += (_, _) => { dragging = true; Paint(); };
        thumb.DragCompleted += (_, _) => { dragging = false; Paint(); };
        thumb.PropertyChanged += (_, e) => { if (e.Property == InputElement.IsPointerOverProperty) Paint(); };
        var track = new Track
        {
            Name = "PART_Track", Orientation = Orientation.Horizontal, DecreaseButton = progress, IncreaseButton = rest, Thumb = thumb,
            [!Track.MinimumProperty] = slider[!RangeBase.MinimumProperty], [!Track.MaximumProperty] = slider[!RangeBase.MaximumProperty],
            [!!Track.ValueProperty] = slider[!!RangeBase.ValueProperty], [!Track.IsDirectionReversedProperty] = slider[!Slider.IsDirectionReversedProperty],
        };
        track.RegisterInNameScope(scope);
        return new Grid { Background = Brushes.Transparent, Children = { track } };
    });
}
