using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Data;

namespace EtherlightingEvolved.Controls;

public sealed class WindowButtons : StackPanel
{
    public WindowButtons(Window window)
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        var definitions = new[]
        {
            ("Close", "#FF5F57", "M 4,4 L 8,8 M 8,4 L 4,8"),
            ("Minimize", "#FEBC2E", "M 3,6 L 9,6"),
            ("Zoom", "#28C840", "M 3,7 L 3,3 L 7,3 M 9,5 L 9,9 L 5,9")
        };
        foreach (var definition in definitions)
        {
            var button = new Button
            {
                Width = 12, Height = 12, MinHeight = 0, MinWidth = 0,
                Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(0),
                Background = (SolidColorBrush)new BrushConverter().ConvertFromString(definition.Item2)!,
                ToolTip = definition.Item1, Focusable = false
            };
            var grid = new FrameworkElementFactory(typeof(Grid));
            var circle = new FrameworkElementFactory(typeof(Ellipse));
            System.Windows.Automation.AutomationProperties.SetName(button, definition.Item1);
            var activeColor = ((SolidColorBrush)button.Background).Color;
            window.Deactivated += (_, _) => button.Background = new SolidColorBrush(Color.FromRgb(88, 90, 99));
            window.Activated += (_, _) => button.Background = new SolidColorBrush(activeColor);
            circle.SetBinding(Shape.FillProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            circle.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromArgb(34, 0, 0, 0)));
            circle.SetValue(Shape.StrokeThicknessProperty, .6);
            grid.AppendChild(circle);
            var symbol = new FrameworkElementFactory(typeof(Path));
            symbol.Name = "Symbol";
            symbol.SetValue(Path.DataProperty, Geometry.Parse(definition.Item3));
            symbol.SetValue(Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(60, 40, 22)));
            symbol.SetValue(Shape.StrokeThicknessProperty, 1.1);
            symbol.SetValue(OpacityProperty, 0.0);
            grid.AppendChild(symbol);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = grid };
            button.Click += (_, _) =>
            {
                if (definition.Item1 == "Close") window.Close();
                else if (definition.Item1 == "Minimize") window.WindowState = WindowState.Minimized;
                else window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            };
            WindowChrome.SetIsHitTestVisibleInChrome(button, true);
            Motion.Attach(button); Children.Add(button);
        }
        MouseEnter += (_, _) => Reveal(true);
        MouseLeave += (_, _) => Reveal(false);
    }

    internal void SetHoverPreview(bool visible) => Reveal(visible);

    private void Reveal(bool visible)
    {
        foreach (Button button in Children)
            if (button.Template.FindName("Symbol", button) is Path symbol)
                symbol.BeginAnimation(OpacityProperty, new DoubleAnimation(visible ? .78 : 0, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 90 : 0)));
    }
}
