using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace EtherlightingEvolved.Native.Shell;

/// <summary>
/// One of the title bar's window buttons: a 12px circle (close red, minimize amber, maximize green) whose glyph
/// shows while the pointer is over the group, drawn as in style.css.
/// </summary>
public sealed class TrafficLight : Control
{
    public enum Kind { Close, Minimize, Maximize }

    static readonly IBrush Rim = new SolidColorBrush(Color.Parse("#22000000"));
    static readonly IBrush Gloss = new SolidColorBrush(Color.Parse("#20ffffff"));
    static readonly IPen GlyphPen = new Pen(new SolidColorBrush(Color.Parse("#cc281c19")), 1);
    static readonly IPen MaximizePen = new Pen(new SolidColorBrush(Color.Parse("#cc153624")), 1);
    readonly Kind kind;
    readonly Action action;
    bool showGlyph, pressed;

    public TrafficLight(Kind kind, string label, Action action)
    {
        this.kind = kind;
        this.action = action;
        Width = Height = 12;
        Cursor = Cursor.Default;
        Focusable = true;
        Avalonia.Automation.AutomationProperties.SetName(this, label);
        ToolTip.SetTip(this, label);
    }

    public bool ShowGlyph
    {
        get => showGlyph;
        set { showGlyph = value; InvalidateVisual(); }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        pressed = true;
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressed) return;
        pressed = false;
        InvalidateVisual();
        e.Handled = true;
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this))) action();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        action();
    }

    public override void Render(DrawingContext context)
    {
        var color = kind switch { Kind.Close => Color.Parse("#ff5f57"), Kind.Minimize => Color.Parse("#febc2e"), _ => Color.Parse("#28c840") };
        if (pressed) color = Color.FromRgb((byte)(color.R * 0.85), (byte)(color.G * 0.85), (byte)(color.B * 0.85));
        var center = new Point(6, 6);
        context.DrawEllipse(new SolidColorBrush(color), new Pen(Rim, 1), center, 5.5, 5.5);
        context.DrawLine(new Pen(Gloss, 1), new Point(3.5, 1.2), new Point(8.5, 1.2));
        if (!showGlyph && !IsFocused) return;
        switch (kind)
        {
            case Kind.Close:
                context.DrawLine(GlyphPen, new Point(3.5, 3.5), new Point(8.5, 8.5));
                context.DrawLine(GlyphPen, new Point(8.5, 3.5), new Point(3.5, 8.5));
                break;
            case Kind.Minimize:
                context.DrawLine(GlyphPen, new Point(2.5, 6), new Point(9.5, 6));
                break;
            default:
                context.DrawLine(MaximizePen, new Point(3, 3), new Point(3, 8));
                context.DrawLine(MaximizePen, new Point(3, 3), new Point(8, 3));
                context.DrawLine(MaximizePen, new Point(9, 4), new Point(9, 9));
                context.DrawLine(MaximizePen, new Point(4, 9), new Point(9, 9));
                break;
        }
    }
}
