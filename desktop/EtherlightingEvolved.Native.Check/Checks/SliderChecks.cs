using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class SliderChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check)
    {
        if (shell.Workspace.Session.Bool("simulated") != true || TopLevel.GetTopLevel(shell) is not Window window)
            throw new InvalidOperationException("Port slider checks require the simulator window.");
        var port = PortTopology.LightingPorts(shell.Workspace.State).Copper.First();
        var definition = EffectRenderer.EffectById("static");
        var brightness = definition.Objects("params").First(p => p.Str("type") == "color").Str("brightness_param")!;
        var effect = EffectRenderer.DefaultConfig("static");
        effect["ports"] = Js.NumberArray(new[] { port });
        void ClickButton(string name) => shell.GetLogicalDescendants().OfType<Button>()
            .First(b => b.IsEffectivelyVisible && Equals(b.Content, name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            await shell.Studio.ChooseEffect(effect);
            Dispatcher.UIThread.RunJobs();
            var slider = shell.GetVisualDescendants().OfType<Slider>()
                .Single(control => control.IsEffectivelyVisible && AutomationProperties.GetName(control) == "Color level");
            var track = slider.GetVisualDescendants().OfType<Track>().Single();
            var thumb = track.Thumb!;
            var knob = thumb.GetVisualDescendants().OfType<Border>().Single();
            var top = knob.TranslatePoint(default, slider)!.Value.Y;
            check(knob.Bounds.Height == 12 && top >= 0 && top + knob.Bounds.Height <= slider.Bounds.Height
                && Math.Abs(top + 6 - slider.Bounds.Height / 2) < 1, "the entire port slider thumb is centered inside its hit area");

            var railHeight = track.Bounds.Height;
            Point At(double fraction, double y) => track.TranslatePoint(new Point(
                thumb.Bounds.Width / 2 + (track.Bounds.Width - thumb.Bounds.Width) * fraction, y), window)!.Value;
            double? Staged() => EffectDraft.EffectDraftForPort(shell.Studio.Draft, port).Num("params." + brightness);
            var first = At(.28, railHeight / 2 - 4);
            window.MouseMove(first);
            window.MouseDown(first, MouseButton.Left);
            window.MouseUp(first, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            check(Math.Abs(slider.Value - 28) <= 1 && Staged() == slider.Value,
                "clicking above the painted rail changes the selected port's effect level");

            var start = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            var end = At(.77, railHeight / 2);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            check(Math.Abs(slider.Value - 77) <= 1 && Staged() == slider.Value,
                "dragging the port slider thumb updates the same effect draft");
        }
        finally
        {
            if (shell.Studio.HasEdits) ClickButton("Discard");
            ClickButton("Ethernet");
        }
    }
}
