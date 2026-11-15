using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The Fans page against the demo switch: live readings, each mode, the curves and both sliders.</summary>
public static class FansPageChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check)
    {
        if (!shell.Workspace.Api.BaseUri.IsLoopback || shell.Workspace.Session.Bool("simulated") != true)
            throw new InvalidOperationException("These fan checks require a local demo switch.");
        Control Page() => shell.CurrentPage ?? throw new InvalidOperationException("No workspace page");
        static IEnumerable<T> All<T>(Control root) where T : Control => root.GetLogicalDescendants().OfType<T>();
        static string? Label(Button button) => button.Content as string
            ?? (button.Content as StackPanel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text;
        static Button? Find(Control root, string label) => All<Button>(root).FirstOrDefault(b => Label(b) == label && b.IsEffectivelyVisible);
        static async Task<bool> Until(Func<Task<bool>> predicate, double seconds = 8)
        {
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                if (await predicate()) return true;
                await Task.Delay(50);
            }
            return false;
        }
        async Task<JsonNode?> Fans() => await shell.Workspace.Request("/api/fans");
        async Task<bool> Click(string label)
        {
            if (!await Until(() => Task.FromResult(Find(Page(), label)?.IsEffectivelyEnabled == true))) return false;
            Find(Page(), label)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return true;
        }
        bool Shows(string prefix) => All<TextBlock>(Page()).Any(text => text.IsEffectivelyVisible && (text.Text ?? "").StartsWith(prefix, StringComparison.Ordinal));

        await shell.Navigate("Fans");
        check(await Until(() => Task.FromResult(Shows("Switch control") && All<TextBlock>(Page()).Any(t => (t.Text ?? "").EndsWith(" °C", StringComparison.Ordinal)))),
            "Fans shows the switch temperatures and the fan modes");

        check(await Click("Thermal target") && await Until(async () => (await Fans()).Str("settings.mode") == "target"),
            "Thermal target starts fan control on the switch");
        var described = await Until(() => Task.FromResult(Shows("Keeping the CPU at 55 °C")));
        check(described, "the page describes the thermal target it holds"
                         + (described ? "" : $" ({string.Join(" | ", All<TextBlock>(Page()).Where(t => t.IsEffectivelyVisible).Select(t => t.Text).Where(t => t?.Length > 20))})"));
        var target = All<Slider>(Page()).First(slider => slider.IsEffectivelyVisible);
        target.Value = 61;
        check(await Until(async () => (await Fans()).Int("settings.target_celsius") == 61), "moving the target slider sets the target temperature");

        check(await Click("Manual") && await Until(async () => (await Fans()).Str("settings.mode") == "manual"), "Manual holds a fixed fan speed");
        var speed = All<Slider>(Page()).First(slider => slider.IsEffectivelyVisible);
        speed.Value = 82;
        check(await Until(async () => (await Fans()) is { } fans && fans.Int("settings.manual_percent") == 82
                                       && fans["reading"]?["fans"]?.AsArray().All(fan => fan.Int("percent") == 82) == true),
            "moving the speed slider sets both fans");
        check(await Until(() => Task.FromResult(Shows("82%"))), "the fan reading follows the new speed");

        check(await Click("Curve") && await Click("High") && await Until(async () => (await Fans()) is { } fans
                                                                    && fans.Str("settings.mode") == "curve" && fans.Str("settings.curve") == "high"),
            "a fan curve is chosen from the four curves");
        check(await Until(() => Task.FromResult(Shows("Following the High curve"))), "the page names the curve the fans follow");

        var refused = await shell.Workspace.Api.RequestAsync("/api/fans", "PUT", new JsonObject { ["mode"] = "manual", ["manual_percent"] = 12 }, shell.Workspace.WorkspaceId)
            .ContinueWith(task => task.Exception?.InnerException as ApiException);
        check(refused is { Status: 400 } && (await Fans()).Str("settings.curve") == "high", "a speed below 30% is refused and the curve stays");

        check(await Click("Switch control") && await Until(async () => (await Fans()) is { } fans
                                                                       && fans.Str("settings.mode") == "switch" && fans.Str("reading.controller") == "absent"),
            "Switch control returns the fans to the switch");
    }
}
