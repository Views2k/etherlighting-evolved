using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using EtherlightingEvolved.Services;

namespace EtherlightingEvolved.Views;

public sealed class GroupsDialog : Window
{
    private readonly EngineClient client;
    private readonly StackPanel groupsPanel = new();
    private readonly StackPanel portsPanel = new();
    private readonly TextBlock status = ViewUi.Status();
    private readonly List<GroupRow> groups = new();
    private readonly Dictionary<int, ComboBox> assignments = new();
    private readonly Button save;
    private int copperPortCount;
    private bool ready;
    private bool dirty;
    private bool saving;

    public GroupsDialog(EngineClient client)
    {
        this.client = client;
        Title = "Port groups"; Width = 700; Height = 620; MinWidth = 580; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; Background = ViewUi.Theme("SurfaceBrush"); Foreground = ViewUi.Theme("TextBrush");
        var root = new DockPanel { Margin = new Thickness(24) };
        var heading = ViewUi.Header("Port groups", "Assign ports to groups that share an effect.");
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ViewUi.Button("Cancel", Close));
        save = ViewUi.Button("Save groups", SaveAsync); save.IsEnabled = false; buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons); footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        var names = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
        names.Children.Add(ViewUi.Text("Groups", 14)); names.Children.Add(groupsPanel);
        names.Children.Add(ViewUi.Button("+ Add group", AddGroup));
        var nameScroll = new ScrollViewer { Content = names, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        grid.Children.Add(nameScroll);
        var portScroll = new ScrollViewer { Content = portsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(portScroll, 1); grid.Children.Add(portScroll);
        root.Children.Add(grid); ViewUi.DialogChrome(this, root, "Port groups");
        Loaded += async (_, _) => await ReloadAsync();
        Closing += (_, args) =>
        {
            if (saving) { args.Cancel = true; return; }
            if (dirty && DialogResult != true && !ViewUi.Confirm(this, "Discard group changes?", "Your unsaved group changes will be discarded.", "Discard")) args.Cancel = true;
        };
    }

    public async Task ReloadAsync()
    {
        try
        {
            var state = await client.GetAsync("/api/state");
            var zones = state?["zones"] as JsonArray ?? new();
            groups.Clear(); groupsPanel.Children.Clear(); portsPanel.Children.Clear(); assignments.Clear();
            foreach (var zone in zones.OfType<JsonObject>()) AddGroupRow((JsonObject)zone.DeepClone());
            copperPortCount = ViewUi.Int(state, "num_ports");
            var opticalPorts = (state?["sfp"] as JsonArray ?? new()).OfType<JsonObject>()
                .Select(light => ViewUi.Int(light, "port")).Where(port => port > copperPortCount).ToHashSet();
            portsPanel.Children.Add(ViewUi.Text("Port assignments", 14));
            foreach (int port in Enumerable.Range(1, copperPortCount).Concat(opticalPorts.OrderBy(port => port)))
            {
                var selected = groups.FirstOrDefault(g => (g.Original["ports"] as JsonArray)?.Any(p => p!.GetValue<int>() == port) == true);
                var box = new ComboBox();
                FillChoices(box, selected);
                box.SelectionChanged += (_, _) => { if (ready) dirty = true; };
                assignments.Add(port, box);
                var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
                string portLabel = opticalPorts.Contains(port) ? $"SFP {port}" : $"Port {port}";
                var label = ViewUi.Text(portLabel, 13); label.Width = 66;
                DockPanel.SetDock(label, Dock.Left); row.Children.Add(label); row.Children.Add(box); portsPanel.Children.Add(row);
                System.Windows.Automation.AutomationProperties.SetName(box, $"Group for {portLabel}");
            }
            ready = true; dirty = false; save.IsEnabled = true;
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    private void AddGroup()
    {
        var group = AddGroupRow(new JsonObject { ["id"] = Guid.NewGuid().ToString("N")[..12], ["name"] = "New group", ["ports"] = new JsonArray(), ["effect"] = new JsonObject { ["id"] = "static", ["params"] = new JsonObject { ["color"] = "#ffffff" } } });
        foreach (var combo in assignments.Values) { var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as GroupRow; FillChoices(combo, selected); }
        dirty = true; group.Name.Focus(); group.Name.SelectAll();
    }

    private GroupRow AddGroupRow(JsonObject spec)
    {
        var row = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var group = new GroupRow(spec, new TextBox { Text = ViewUi.Str(spec, "name"), MaxLength = 64 });
        group.Name.TextChanged += (_, _) => { if (ready) dirty = true; };
        var remove = ViewUi.Button("−", () => RemoveGroup(group, row), "Delete group");
        DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove); row.Children.Add(group.Name);
        groups.Add(group); groupsPanel.Children.Add(row); return group;
    }

    private void RemoveGroup(GroupRow group, DockPanel row)
    {
        if (assignments.Values.Any(box => ReferenceEquals((box.SelectedItem as ComboBoxItem)?.Tag, group)))
        {
            status.Text = "Assign this group's ports to another group before deleting it."; return;
        }
        groups.Remove(group); groupsPanel.Children.Remove(row);
        foreach (var combo in assignments.Values) { var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as GroupRow; FillChoices(combo, selected); }
        status.Text = ""; dirty = true;
    }

    private void FillChoices(ComboBox combo, GroupRow? selected)
    {
        combo.Items.Clear(); combo.Items.Add(new ComboBoxItem { Content = "Unassigned", Tag = null });
        foreach (var group in groups)
        {
            var label = new TextBlock(); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBox.Text)) { Source = group.Name });
            var item = new ComboBoxItem { Content = label, Tag = group }; combo.Items.Add(item);
            if (ReferenceEquals(group, selected)) combo.SelectedItem = item;
        }
        if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
    }

    private async Task SaveAsync()
    {
        if (!ready) return;
        try
        {
            var zones = new JsonArray();
            var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
            foreach (var group in groups)
            {
                string name = group.Name.Text.Trim();
                if (name.Length == 0) throw new InvalidOperationException("Give every group a name.");
                if (!names.Add(name)) throw new InvalidOperationException("Use a different name for each group.");
                var ports = assignments.Where(p => ReferenceEquals((p.Value.SelectedItem as ComboBoxItem)?.Tag, group)).Select(p => p.Key).ToArray();
                if (ports.Length == 0) throw new InvalidOperationException($"Assign a port to {name}, or remove that group.");
                var zone = (JsonObject)group.Original.DeepClone();
                zone["name"] = name; zone["ports"] = new JsonArray(ports.Select(p => JsonValue.Create(p)).ToArray()); zones.Add(zone);
            }
            if (zones.Count == 0 && copperPortCount > 0) throw new InvalidOperationException("Create at least one group.");
            saving = true;
            await client.SendAsync(HttpMethod.Put, "/api/zones", new JsonObject { ["zones"] = zones });
            saving = false; dirty = false; DialogResult = true;
        }
        catch (Exception ex) { saving = false; ViewUi.Error(status, ex); }
    }

    private sealed record GroupRow(JsonObject Original, TextBox Name);

    public async Task VerifyAsync()
    {
        ViewUi.RequireDemo(client);
        await ViewUi.WaitAsync(() => ready);
        var original = (await client.GetAsync("/api/zones"))!.DeepClone();
        string title = "Verification group";
        ViewUi.Click(this, "+ Add group");
        groups[^1].Name.Text = title;
        foreach (int port in new[] { 8, 9 })
        {
            var assignment = assignments[port];
            assignment.SelectedItem = assignment.Items.OfType<ComboBoxItem>().First(x => ReferenceEquals(x.Tag, groups[^1]));
        }
        ViewUi.Click(this, "Save groups");
        await ViewUi.WaitAsync(() => DialogResult == true);
        var saved = (await client.GetAsync("/api/zones") as JsonArray)!;
        if (!saved.OfType<JsonObject>().Any(g => ViewUi.Str(g, "name") == title && g["ports"]?.ToJsonString() == "[8,9]"))
            throw new InvalidOperationException("Group assignment did not persist.");
        await client.SendAsync(HttpMethod.Put, "/api/zones", new JsonObject { ["zones"] = original });
    }
}
