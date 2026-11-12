using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

public static class AutomationProfileChecks
{
    public static async Task RunAsync(AppShell shell, Action<bool, string> check)
    {
        if (!shell.Workspace.Api.BaseUri.IsLoopback || shell.Workspace.Session.Bool("simulated") != true)
            throw new InvalidOperationException("These editing checks require a local demo switch.");
        var key = Guid.NewGuid().ToString("N")[..8];
        var profile = "Desk " + key;
        var rule = "Traffic " + key;
        var schedule = "Weekend " + key;
        Control Page() => shell.CurrentPage ?? throw new InvalidOperationException("No workspace page");
        static IEnumerable<T> All<T>(Control root) where T : Control => root.GetLogicalDescendants().OfType<T>();
        static Button? Find(Control root, string label) => All<Button>(root).FirstOrDefault(b => b.Content as string == label && b.IsEffectivelyVisible);
        static Task Wait(Func<bool> predicate, string purpose) => WaitAsync(() => Task.FromResult(predicate()), purpose);
        static async Task WaitAsync(Func<Task<bool>> predicate, string purpose)
        {
            var until = Stopwatch.StartNew();
            while (!await predicate())
            {
                if (until.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Timed out waiting for " + purpose);
                await Task.Delay(20);
            }
        }
        static async Task Click(Control root, string label)
        {
            await Wait(() => Find(root, label)?.IsEffectivelyEnabled == true, label);
            Find(root, label)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        static bool Contains(Control root, string label) => All<TextBlock>(root).Any(t => t.Text == label && t.IsEffectivelyVisible);
        async Task<JsonNode?> Read(string url) => await shell.Workspace.Request(url);
        async Task Named(string title, string value)
        {
            await Wait(() => Find(shell.Overlay, title) != null && All<TextBox>(shell.Overlay).Any(t => t.IsEffectivelyVisible), title + " name form");
            var box = All<TextBox>(shell.Overlay).First(t => t.IsEffectivelyVisible);
            box.Text = value;
            await Click(shell.Overlay, title);
            await Wait(() => !shell.Overlay.HasDialog, "dialog closed");
        }
        async Task HasRow(string endpoint, string path, string name, bool exists)
        {
            await WaitAsync(async () => (await Read(endpoint)).Objects(path).Any(r => r.Str("name") == name) == exists, name + " in simulator");
        }
        await shell.Navigate("Profiles");
        await Click(Page(), "Save current as profile");
        await Named("Save profile", profile);
        await HasRow("/api/profiles", "profiles", profile, true);
        await Wait(() => Contains(Page(), profile), "profile shown in native list");
        check(true, "Profiles saves the current setup through the native name dialog");
        int initialBrightness = (await Read("/api/state")).Int("brightness") ?? 100;
        int changedBrightness = initialBrightness == 36 ? 72 : 36;
        await shell.Workspace.Request("/api/brightness", "PUT", new JsonObject { ["value"] = changedBrightness });
        await shell.Workspace.Refresh(false);
        await Click(Page(), "Apply profile");
        await WaitAsync(async () => (await Read("/api/state")).Int("brightness") == initialBrightness, "profile scene applied");
        await Click(Page(), "Undo last apply");
        await WaitAsync(async () => (await Read("/api/state")).Int("brightness") == changedBrightness, "profile undo restored the prior scene");
        await Click(Page(), "Apply profile");
        await WaitAsync(async () => (await Read("/api/state")).Int("brightness") == initialBrightness, "restored original brightness");
        check(true, "Applying and undoing the profile restores the two exact brightness settings");
        await Click(Page(), "Duplicate");
        await Named("Duplicate profile", profile + " copy");
        await HasRow("/api/profiles", "profiles", profile + " copy", true);
        await Click(Page(), "Rename");
        await Named("Rename profile", profile + " renamed");
        await HasRow("/api/profiles", "profiles", profile + " renamed", true);
        await Click(Page(), "Delete"); await Click(shell.Overlay, "Delete");
        await HasRow("/api/profiles", "profiles", profile + " renamed", false);
        check(true, "Profiles duplicates, renames and deletes the selected record");
        await Wait(() => Find(Page(), "Refresh profiles")?.IsEffectivelyEnabled == true, "Profiles saved");
        await shell.Navigate("Automation");
        await Click(Page(), "New rule");
        All<TextBox>(Page()).Single(t => AutomationProperties.GetName(t) == "Rule name").Text = rule;
        await Click(Page(), "Add condition");
        await Click(Page(), "Schedules");
        await Wait(() => shell.Overlay.HasDialog, "unsaved rule confirmation");
        await Click(shell.Overlay, "Cancel");
        check(All<TextBox>(Page()).Any(t => t.Text == rule), "Canceling navigation retains the rule draft");
        if (Find(Page(), "Ports") != null) await Click(Page(), "Ports");
        await Click(Page(), "None");
        check(Find(Page(), "Apply rule")?.IsEnabled == false, "Empty rule scope cannot be applied");
        await Click(Page(), "All ports");
        if (Find(Page(), "Appearance") != null) await Click(Page(), "Appearance");
        var swatch = All<Button>(Page()).Single(b => AutomationProperties.GetName(b) == "Rule color");
        swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => Contains(shell.Overlay, "Favorites") && Contains(shell.Overlay, "Palette"), "shared rule color library");
        await Click(shell.Overlay, "Done");
        check(true, "Rule appearance opens the same favorites and named palettes as Lighting");
        await Click(Page(), "Apply rule");
        await HasRow("/api/scene", "scene.rules", rule, true);
        await Click(Page(), "Test rules");
        await Wait(() => All<TextBlock>(shell.Overlay).Any(t => t.Text?.StartsWith("Displayed lighting:", StringComparison.Ordinal) == true), "simulator rule results");
        await Click(shell.Overlay, "Done");
        check(true, "The applied rule can be tested against a simulator port");
        await Click(Page(), "Delete rule"); await Click(shell.Overlay, "Delete");
        await HasRow("/api/scene", "scene.rules", rule, false);
        await Click(Page(), "Schedules");
        await Click(Page(), "New schedule");
        All<TextBox>(Page()).Single(t => AutomationProperties.GetName(t) == "Schedule name").Text = schedule;
        All<NumericUpDown>(Page()).First(t => t.IsEffectivelyVisible).Value = initialBrightness;
        check(All<TextBlock>(Page()).Any(t => t.Text?.Contains("Time on the EE computer", StringComparison.Ordinal) == true), "The schedule editor identifies the scheduler's clock");
        await Click(Page(), "Save schedule");
        await HasRow("/api/schedules", "schedules", schedule, true);
        await Click(Page(), "Run now"); await Click(shell.Overlay, "Run now");
        await WaitAsync(async () => (await Read("/api/schedules")).Objects("schedules").FirstOrDefault(s => s.Str("name") == schedule).At("last_run.at") != null, "schedule run receipt");
        await Click(Page(), "Delete schedule"); await Click(shell.Overlay, "Delete");
        await HasRow("/api/schedules", "schedules", schedule, false);
        check(true, "Weekly schedules save, run and delete through native controls");
        await Wait(() => Find(Page(), "Refresh schedules")?.IsEffectivelyEnabled == true, "finished schedule operation");
        await shell.Navigate("Profiles");
        await Wait(() => Contains(Page(), profile), "saved profile list");
        var own = All<Button>(Page()).First(b => b.Content is Control content && Contains(content, profile));
        own.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Click(Page(), "Delete"); await Click(shell.Overlay, "Delete"); await HasRow("/api/profiles", "profiles", profile, false);
        await Wait(() => Find(Page(), "Refresh profiles")?.IsEffectivelyEnabled == true, "finished profile operation");
    }
}
