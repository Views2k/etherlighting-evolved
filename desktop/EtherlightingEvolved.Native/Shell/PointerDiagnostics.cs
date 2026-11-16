using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace EtherlightingEvolved.Native.Shell;

/// <summary>
/// An optional record of pointer input, for diagnosing touch from a remote display such as a Moonlight stream.
/// It is off unless a file named pointer-diagnostics.on exists in the app data folder when EE starts; then each
/// press, release, capture loss, click and tooltip, and moves at most five times a second, are appended to
/// pointer-diagnostics.log beside it. It only observes events and never marks them handled.
/// </summary>
public static class PointerDiagnostics
{
    public static void Attach(Window window)
    {
        var folder = Locations.AppData;
        if (!File.Exists(Path.Combine(folder, "pointer-diagnostics.on"))) return;
        StreamWriter writer;
        try { writer = new StreamWriter(Path.Combine(folder, "pointer-diagnostics.log"), append: true) { AutoFlush = true }; }
        catch (Exception) { return; }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastMove = TimeSpan.Zero;
        void Write(string text)
        {
            try { writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}"); } catch (Exception) { }
        }
        static string Describe(object? source)
        {
            if (source is not Visual visual) return source?.GetType().Name ?? "none";
            var parts = new List<string>();
            foreach (var item in new[] { visual }.Concat(visual.GetVisualAncestors()).Take(4))
            {
                var name = item is Control control ? Avalonia.Automation.AutomationProperties.GetName(control) : null;
                var text = item is ContentControl { Content: string content } ? content : item is TextBlock block ? block.Text : null;
                parts.Add(item.GetType().Name + (string.IsNullOrEmpty(name ?? text) ? "" : $"('{name ?? text}')"));
            }
            return string.Join(" < ", parts);
        }
        string Line(string kind, PointerEventArgs e)
        {
            var point = e.GetPosition(window);
            var properties = e.GetCurrentPoint(window).Properties;
            return $"{kind} {e.Pointer.Type} id={e.Pointer.Id} at {point.X:0},{point.Y:0} left={properties.IsLeftButtonPressed} " +
                   $"handled={e.Handled} captured={Describe(e.Pointer.Captured)} source={Describe(e.Source)}";
        }
        Write($"started; window {window.Bounds.Width:0}x{window.Bounds.Height:0} at {window.Position}, scaling {window.RenderScaling}");
        // Bubble with handledEventsToo: the line shows whether something on the way handled the event.
        window.AddHandler(InputElement.PointerPressedEvent, (_, e) => Write(Line("press", e)), RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.PointerReleasedEvent, (_, e) => Write(Line("release", e)), RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.PointerCaptureLostEvent, (_, e) => Write($"capture-lost {e.Pointer.Type} id={e.Pointer.Id} source={Describe(e.Source)}"),
            RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.PointerMovedEvent, (_, e) =>
        {
            if (clock.Elapsed - lastMove < TimeSpan.FromMilliseconds(200)) return;
            lastMove = clock.Elapsed;
            Write(Line("move", e));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(InputElement.TappedEvent, (_, e) => Write($"tapped source={Describe(e.Source)}"), RoutingStrategies.Bubble, handledEventsToo: true);
        window.AddHandler(Button.ClickEvent, (_, e) => Write($"click source={Describe(e.Source)}"), RoutingStrategies.Bubble, handledEventsToo: true);
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (TopLevel.GetTopLevel(control) == window) Write($"tooltip {(ToolTip.GetIsOpen(control) ? "open" : "closed")} on {Describe(control)}");
        });
        window.Closed += (_, _) => { try { writer.Dispose(); } catch (Exception) { } };
    }
}
