using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EtherlightingEvolved.Services;
using EtherlightingEvolved.Controls;

namespace EtherlightingEvolved.Views;

public sealed class ScenesView : UserControl
{
    private readonly EngineClient client;
    private readonly ListBox list = new();
    private readonly StackPanel detail = new();
    private readonly TextBox name = new() { MaxLength = 64, MinWidth = 180 };
    private readonly TextBlock status = ViewUi.Status();
    private JsonObject scenes = new();
    private string? selected;
    private bool loading;

    public ScenesView(EngineClient client)
    {
        this.client = client;
        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 20) };
        var heading = ViewUi.Header("Scenes", "Save your colors, effects, groups, and rules together.");
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var saveRow = new DockPanel { Margin = new Thickness(0, 0, 0, 20) };
        var save = ViewUi.Button("Save current lighting", SaveAsync);
        DockPanel.SetDock(save, Dock.Right); saveRow.Children.Add(save);
        saveRow.Children.Add(name); System.Windows.Automation.AutomationProperties.SetName(name, "Scene name");
        DockPanel.SetDock(saveRow, Dock.Top); root.Children.Add(saveRow);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        split.ColumnDefinitions.Add(new ColumnDefinition());
        list.Margin = new Thickness(0, 0, 24, 0);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: string key }) { selected = key; name.Text = key; RenderDetail(); }
        };
        split.Children.Add(list);
        var scroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); split.Children.Add(scroll);
        root.Children.Add(split); Content = root;
    }

    public async Task ReloadAsync()
    {
        if (loading) return;
        loading = true;
        try
        {
            scenes = await client.GetAsync("/api/presets") as JsonObject ?? new();
            list.Items.Clear();
            foreach (var pair in scenes.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var row = new StackPanel { Margin = new Thickness(4, 9, 4, 9) };
                row.Children.Add(ViewUi.Text(pair.Key, 14));
                int groupCount = (pair.Value?["zones"] as JsonArray)?.Count ?? 0;
                row.Children.Add(ViewUi.Text($"{groupCount} {(groupCount == 1 ? "group" : "groups")} · {ViewUi.Number(pair.Value, "brightness", 100):0}% brightness", 11, true));
                var item = new ListBoxItem { Content = row, Tag = pair.Key };
                list.Items.Add(item);
                if (pair.Key == selected) list.SelectedItem = item;
            }
            if (selected == null || !scenes.ContainsKey(selected))
            {
                selected = null;
                if (list.Items.Count > 0) list.SelectedIndex = 0;
            }
            RenderDetail();
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
        finally { loading = false; }
    }

    private void RenderDetail()
    {
        detail.Children.Clear();
        Motion.Enter(detail);
        if (selected == null || scenes[selected] is not JsonObject scene)
        {
            detail.Children.Add(ViewUi.Header("Your lighting, saved", "Enter a scene name and save the current lighting setup. Choose a saved scene to restore it."));
            return;
        }
        detail.Children.Add(ViewUi.Header(selected, "Saved lighting setup"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(ViewUi.Button("Apply scene", ApplyAsync));
        actions.Children.Add(ViewUi.Button("Delete", DeleteAsync));
        detail.Children.Add(actions);
        detail.Children.Add(ViewUi.Separator());
        detail.Children.Add(ViewUi.Text("Ethernet groups", 13));
        foreach (var zone in (scene["zones"] as JsonArray ?? new()).OfType<JsonObject>())
        {
            string effectId = ViewUi.Str(zone["effect"], "id");
            string effectName = client.Effects.OfType<JsonObject>().FirstOrDefault(e => ViewUi.Str(e, "id") == effectId)?["name"]?.ToString() ?? ViewUi.Str(zone["effect"], "name", effectId.Replace('_', ' '));
            var row = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var text = new StackPanel();
            text.Children.Add(ViewUi.Text(ViewUi.Str(zone, "name"), 14));
            text.Children.Add(ViewUi.Text(effectName + " · Ports " + string.Join(", ", (zone["ports"] as JsonArray ?? new()).Select(p => p?.ToString())), 12, true));
            row.Children.Add(text); detail.Children.Add(row);
        }
        var sfp = scene["sfp"] as JsonObject;
        if (sfp?.Count > 0)
        {
            detail.Children.Add(ViewUi.Separator());
            detail.Children.Add(ViewUi.Text("SFP indicators", 13));
            foreach (var pair in sfp)
            {
                if (pair.Value is not JsonObject item) continue;
                string mode = ViewUi.Str(item, "mode");
                string effect = ViewUi.Str(item, "effect", "steady") switch { "pulse" => "Gentle pulse", "two_color" => "Two-color fade", "color_cycle" => "Color cycle", _ => "Steady color" };
                detail.Children.Add(ViewUi.Text($"SFP {pair.Key} · {(mode == "off" ? "Off" : mode == "unmanaged" ? "Link status" : effect)}", 12, true));
            }
        }
        int rules = (scene["rules"] as JsonArray)?.Count ?? 0;
        int manual = (scene["overrides"] as JsonObject)?.Count ?? 0;
        detail.Children.Add(ViewUi.Separator());
        detail.Children.Add(ViewUi.Text($"{rules} {(rules == 1 ? "rule" : "rules")} · {manual} manual {(manual == 1 ? "color" : "colors")}", 12, true));
    }

    private async Task SaveAsync()
    {
        string title = name.Text.Trim();
        if (title.Length == 0) { status.Text = "Enter a name for this scene."; name.Focus(); return; }
        if (scenes.ContainsKey(title) && !ViewUi.Confirm(this, "Replace scene?", "The existing scene will be replaced with your current lighting setup.", "Replace")) return;
        try
        {
            await client.SendAsync(HttpMethod.Post, "/api/presets", new { name = title });
            selected = title; await ReloadAsync(); status.Text = "Scene saved.";
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    private async Task ApplyAsync()
    {
        if (selected == null) return;
        try { await client.SendAsync(HttpMethod.Post, "/api/presets/" + Uri.EscapeDataString(selected) + "/apply"); status.Text = "Scene applied."; }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    private async Task DeleteAsync()
    {
        if (selected == null || !ViewUi.Confirm(this, "Delete scene?", "This saved scene will be removed. Your current lighting will keep running.", "Delete")) return;
        try
        {
            await client.SendAsync(HttpMethod.Delete, "/api/presets/" + Uri.EscapeDataString(selected));
            selected = null; name.Clear(); await ReloadAsync(); status.Text = "Scene deleted.";
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    public async Task VerifyAsync()
    {
        ViewUi.RequireDemo(client);
        await ReloadAsync();
        string title = "Verification " + Guid.NewGuid().ToString("N")[..8];
        int originalBrightness = ViewUi.Int(client.State, "brightness", 100);
        name.Text = title;
        ViewUi.Click(this, "Save current lighting");
        await ViewUi.WaitAsync(() => status.Text == "Scene saved.");
        if ((await client.GetAsync("/api/presets") as JsonObject)?[title] is not JsonObject)
            throw new InvalidOperationException("Saved scene was not persisted.");
        await client.SendAsync(HttpMethod.Put, "/api/brightness", new { value = originalBrightness == 23 ? 37 : 23 });
        ViewUi.Click(this, "Apply scene");
        await ViewUi.WaitAsync(() => status.Text == "Scene applied.");
        if (ViewUi.Int(client.State, "brightness") != originalBrightness) throw new InvalidOperationException("Applying the saved scene did not restore brightness.");
        ViewUi.Click(this, "Delete", "Delete");
        await ViewUi.WaitAsync(() => status.Text == "Scene deleted.");
        if ((await client.GetAsync("/api/presets") as JsonObject)?.ContainsKey(title) == true) throw new InvalidOperationException("Deleted scene remained in storage.");
    }
}
