using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages.Lighting;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class LightingPageChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check)
    {
        var w = shell.Workspace;
        if (w.Session.Bool("simulated") != true) throw new InvalidOperationException("Lighting checks require the simulator.");
        await shell.Navigate("Lighting", true);
        var studio = shell.Studio;
        var send = w.WorkspaceRequest();
        async Task<JsonObject> Scene() => (await send("/api/scene", "GET", null)).Obj();
        IEnumerable<Button> Buttons() => shell.GetLogicalDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible);
        void Click(string label)
        {
            var button = Buttons().First(b => Equals(b.Content, label) && b.IsEnabled);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
        }
        async Task WaitFor(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!condition() && DateTime.UtcNow < until) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
            if (!condition()) throw new TimeoutException("Lighting did not reach the expected state.");
        }
        var initial = await Scene(); var original = SecondaryModel.SceneFingerprint(initial["scene"]);
        try
        {
            check(shell.CurrentPage is LightingPage, "the regular workspace opens the native Lighting page");
            var page = (LightingPage)shell.CurrentPage!;
            var bandwidth = page.GetLogicalDescendants().OfType<UplinkBandwidthMeter>().Single();
            var heading = bandwidth.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Uplink bandwidth");
            var window = (Window)TopLevel.GetTopLevel(page)!;
            var windowSize = (window.Width, window.Height);
            var detached = 0;
            void OnDetach(object? sender, LogicalTreeAttachmentEventArgs args) => detached++;
            bandwidth.DetachedFromLogicalTree += OnDetach;
            try
            {
                window.Width = 1300; window.Height = 800; Dispatcher.UIThread.RunJobs();
                var wideRow = Grid.GetRow(bandwidth);
                window.Width = 1000; window.Height = 410; Dispatcher.UIThread.RunJobs();
                page.Update(); page.Frame();
                check(wideRow == 0 && Grid.GetRow(bandwidth) == 3 && heading.IsEffectivelyVisible && detached == 0,
                    "wide and rack layouts keep the same attached bandwidth meter and visible heading");
            }
            finally
            {
                bandwidth.DetachedFromLogicalTree -= OnDetach;
                (window.Width, window.Height) = windowSize; Dispatcher.UIThread.RunJobs();
            }
            Click("None"); check(studio.Selected.Count == 0, "None clears the port selection");
            Click("Ethernet"); var copper = PortTopology.LightingPorts(w.State).Copper;
            check(studio.Selected.SequenceEqual(copper), "Ethernet selects precisely the switch's copper ports");
            check(await studio.ConfirmDraft(), "confirming an unchanged scene proceeds without a dialog");
            if (copper.Count == 0) throw new InvalidOperationException("The simulator requires an Ethernet port.");
            var cfg = EffectRenderer.DefaultConfig("static"); cfg["ports"] = Js.NumberArray(copper.Take(1)); cfg.Obj("params")["color"] = "#3A4BD0";
            await studio.ChooseEffect(cfg);
            var answer = studio.ConfirmDraft();
            await WaitFor(() => shell.Overlay.HasDialog);
            Click("Keep editing");
            check(!await answer && studio.HasEdits, "a later draft still asks for confirmation and Keep editing retains it");
            check(studio.HasEdits && studio.Selected.SequenceEqual(copper.Take(1)) && studio.PreviewScene() is not null, "a selected effect stages a scoped preview");
            check(SecondaryModel.SceneFingerprint((await Scene())["scene"]) == original, "previewing leaves the simulator scene intact");
            Click("Show applied"); check(Buttons().Any(b => Equals(b.Content, "Show preview")), "applied lighting and preview can be compared");
            Click("Show preview"); Click("Discard"); check(!studio.HasEdits, "Discard removes the preview");
            await studio.ChooseEffect(cfg); Click("Apply changes"); await WaitFor(() => !studio.HasEdits && w.Busy == 0);
            var after = await Scene();
            check(SecondaryModel.SceneFingerprint(after["scene"]) != original && studio.CanUndoApply, "Apply commits the preview and enables Undo");
            await studio.UndoLastApply();
            check(SecondaryModel.SceneFingerprint((await Scene())["scene"]) == original, "Undo restores the exact earlier simulator scene");
            foreach (var title in new[] { "Groups", "History", "Saved selections", "Find port", "Port details", "Map key" })
            {
                Click(title); await WaitFor(() => shell.Overlay.HasDialog); check(shell.Overlay.HasDialog, title + " opens in the native Lighting page");
                shell.Overlay.CloseAll();
            }
            var key = copper[0].ToString();
            var namesBefore = (await send("/api/port-names", "GET", null)).Obj();
            try
            {
                Click("Port details"); Click("Edit port names");
                await WaitFor(() => Buttons().Any(b => Equals(b.Content, "Save names") && b.IsEffectivelyEnabled));
                var auto = shell.Overlay.GetLogicalDescendants().OfType<CheckBox>().Single(c => Equals(c.Content, "Use UniFi port names"));
                auto.IsChecked = false;
                var input = shell.Overlay.GetLogicalDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == $"Port {key} name");
                input.Text = "Simulator lighting name"; Dispatcher.UIThread.RunJobs();
                auto.IsChecked = true; Dispatcher.UIThread.RunJobs();
                check(!input.IsEnabled, "UniFi naming displays the controller label on the naming form");
                auto.IsChecked = false; Dispatcher.UIThread.RunJobs();
                check(input.Text == "Simulator lighting name", "manual rename survives switching the naming mode");
                Click("Save names"); await WaitFor(() => !Buttons().Any(b => Equals(b.Content, "Save names")));
                var persisted = (await send("/api/port-names", "GET", null)).Obj();
                check(persisted.Obj("names").Str(key) == "Simulator lighting name" && persisted.Bool("use_unifi") == false, "a name saved in the native editor is returned by the simulator service");
            }
            finally
            {
                shell.Overlay.CloseAll();
                var afterNames = (await send("/api/port-names", "GET", null)).Obj();
                if (!JsonNode.DeepEquals(afterNames["names"], namesBefore["names"]) || afterNames.Bool("use_unifi") != namesBefore.Bool("use_unifi"))
                {
                    await send("/api/port-names", "PUT", new JsonObject { ["expected_revision"] = afterNames["revision"].Copy(), ["use_unifi"] = namesBefore.Bool("use_unifi") ?? false, ["names"] = new JsonObject { [key] = namesBefore.Obj("names").Str(key) ?? "" } });
                    await w.Refresh(false);
                }
            }
            var optical = PortTopology.LightingPorts(w.State).Sfp;
            if (optical.Count > 0)
            {
                await studio.ChooseSfpEffect(new JsonObject { ["effect"] = "pulse" }, [optical[0]]);
                check(studio.Draft.Obj("sfp").Count > 0 && studio.Draft.Obj("sfp").Any(x => x.Value.Str("mode") == "color" && x.Value.Str("effect") == "pulse"), "an optical gallery choice stages the requested SFP color and motion");
                Click("Discard");
            }
        }
        finally
        {
            shell.Overlay.CloseAll();
            if (studio.HasEdits && Buttons().Any(b => Equals(b.Content, "Discard"))) Click("Discard");
            await w.Idle();
            var now = await Scene();
            if (SecondaryModel.SceneFingerprint(now["scene"]) != original)
            {
                await SceneTransaction.CommitScene(now, initial["scene"], send, false); await w.Refresh();
            }
            Click("Ethernet");
        }
    }
}
