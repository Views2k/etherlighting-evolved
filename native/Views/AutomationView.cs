using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Shell;
using System.Windows.Threading;
using EtherlightingEvolved.Services;
using EtherlightingEvolved.Controls;

namespace EtherlightingEvolved.Views;

public sealed class AutomationView : UserControl
{
    private readonly EngineClient client;
    private readonly ListBox rules = new();
    private readonly StackPanel editor = new();
    private readonly StackPanel footer = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock status = ViewUi.Status();
    private readonly ComboBox templates = new() { MinWidth = 150 };
    private JsonArray fieldSpecs = new();
    private JsonArray ruleData = new();
    private string? selectedId;
    private bool dirty;
    private bool selecting;
    private bool loading;
    private TextBox name = new();
    private TextBox color = new();
    private TextBox cycle = new();
    private CheckBox enabled = new();
    private CheckBox setBrightness = new();
    private Slider brightness = new();
    private ComboBox match = new();
    private ComboBox style = new();
    private readonly List<(int port, CheckBox box)> portBoxes = new();
    private readonly List<ConditionEditor> conditions = new();
    private StackPanel conditionPanel = new();

    public AutomationView(EngineClient client)
    {
        this.client = client;
        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 20) };
        var heading = ViewUi.Header("Automation", "Respond to port status, power, traffic, and time.");
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var library = new DockPanel { Margin = new Thickness(0, 0, 24, 0) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        actions.Children.Add(ViewUi.Button("New rule", () => NewRule()));
        actions.Children.Add(ViewUi.Button("↑", () => Move(-1), "Move rule up"));
        actions.Children.Add(ViewUi.Button("↓", () => Move(1), "Move rule down"));
        DockPanel.SetDock(actions, Dock.Top);
        library.Children.Add(actions);
        var hint = ViewUi.Text("First matching rule takes priority.", 12, true);
        hint.Margin = new Thickness(0, 10, 0, 0);
        DockPanel.SetDock(hint, Dock.Bottom);
        library.Children.Add(hint);
        rules.SelectionChanged += (_, _) => SelectRule();
        library.Children.Add(rules);
        split.Children.Add(library);
        var editorSlot = new Grid();
        var editorArea = new DockPanel { Width = 760, MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        editorSlot.SizeChanged += (_, _) => editorArea.Width = Math.Min(760, editorSlot.ActualWidth);
        var footerBorder = new Border { BorderBrush = ViewUi.Theme("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 14, 0, 0), Child = footer };
        DockPanel.SetDock(footerBorder, Dock.Bottom);
        editorArea.Children.Add(footerBorder);
        var scroller = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        editorArea.Children.Add(scroller);
        editorSlot.Children.Add(editorArea);
        Grid.SetColumn(editorSlot, 1);
        split.Children.Add(editorSlot);
        root.Children.Add(split);
        Content = root;
    }

    public async Task ReloadAsync()
    {
        if (loading) return;
        loading = true;
        try
        {
            var fields = await client.GetAsync("/api/rules/fields");
            fieldSpecs = (fields?["fields"] as JsonArray)?.DeepClone() as JsonArray ?? new();
            var items = await client.GetAsync("/api/rules/templates") as JsonArray ?? new();
            templates.Items.Clear();
            templates.Items.Add(new ComboBoxItem { Content = "Start from a template", Tag = null });
            foreach (var item in items.OfType<JsonObject>())
                templates.Items.Add(new ComboBoxItem { Content = ViewUi.Str(item, "name"), Tag = item.DeepClone() });
            templates.SelectedIndex = 0;
            await RefreshListAsync();
            if (!dirty)
            {
                var selected = ruleData.OfType<JsonObject>().FirstOrDefault(x => ViewUi.Str(x, "id") == selectedId);
                selected ??= ruleData.OfType<JsonObject>().FirstOrDefault();
                if (selected != null) OpenRule(selected); else NewRule();
            }
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
        finally { loading = false; }
    }

    public bool ConfirmNavigation()
    {
        if (!dirty) return true;
        if (!ViewUi.Confirm(this, "Discard changes?", "Your unsaved rule changes will be discarded.", "Discard")) return false;
        dirty = false;
        return true;
    }

    private async Task RefreshListAsync()
    {
        ruleData = await client.GetAsync("/api/rules") as JsonArray ?? new();
        selecting = true;
        rules.Items.Clear();
        foreach (var rule in ruleData.OfType<JsonObject>())
        {
            var row = new StackPanel { Margin = new Thickness(4, 7, 4, 7) };
            row.Children.Add(ViewUi.Text(ViewUi.Str(rule, "name", "Rule"), 14));
            var ports = rule["ports"] as JsonArray;
            var target = ports == null ? "All Ethernet ports" : string.Join(", ", ports.Select(p => ViewUi.PortLabel(p!.GetValue<int>())));
            row.Children.Add(ViewUi.Text((ViewUi.Bool(rule, "enabled", true) ? "Enabled · " : "Disabled · ") + target, 11, true));
            var item = new ListBoxItem { Content = row, Tag = rule };
            rules.Items.Add(item);
            if (ViewUi.Str(rule, "id") == selectedId) rules.SelectedItem = item;
        }
        selecting = false;
    }

    private void SelectRule()
    {
        if (selecting || rules.SelectedItem is not ListBoxItem { Tag: JsonObject rule }) return;
        if (dirty && !ViewUi.Confirm(this, "Discard changes?", "Your unsaved rule changes will be discarded.", "Discard"))
        {
            selecting = true;
            rules.SelectedItem = rules.Items.OfType<ListBoxItem>().FirstOrDefault(x => ViewUi.Str(x.Tag as JsonObject, "id") == selectedId);
            selecting = false;
            return;
        }
        OpenRule(rule);
    }

    private void NewRule()
    {
        if (dirty && !ViewUi.Confirm(this, "Discard changes?", "Your unsaved rule changes will be discarded.", "Discard")) return;
        OpenRule(new JsonObject
        {
            ["name"] = "New rule", ["enabled"] = true, ["match"] = "all",
            ["conditions"] = new JsonArray(new JsonObject { ["field"] = "anomaly", ["op"] = "is", ["value"] = true }),
            ["look"] = new JsonObject { ["color"] = "#ff667e", ["style"] = "solid", ["rate"] = 0.25 }
        });
        selecting = true;
        rules.SelectedIndex = -1;
        selecting = false;
        dirty = false;
        name.Focus();
        name.SelectAll();
    }

    private void OpenRule(JsonObject rule)
    {
        selectedId = rule["id"]?.GetValue<string>();
        editor.Children.Clear();
        footer.Children.Clear();
        conditions.Clear();
        portBoxes.Clear();
        name = new TextBox { Text = ViewUi.Str(rule, "name"), MaxLength = 80 };
        enabled = new CheckBox { Content = "Enabled", IsChecked = ViewUi.Bool(rule, "enabled", true), Margin = new Thickness(16, 14, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new DockPanel();
        DockPanel.SetDock(enabled, Dock.Right); nameRow.Children.Add(enabled);
        nameRow.Children.Add(ViewUi.Label("Rule name", name));
        editor.Children.Add(nameRow);
        var templateRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        if (templates.Parent is Panel oldParent) oldParent.Children.Remove(templates);
        var applyTemplate = ViewUi.Button("Use template", () =>
        {
            if (templates.SelectedItem is not ComboBoxItem { Tag: JsonObject template }) return;
            if (dirty && !ViewUi.Confirm(this, "Replace this draft?", "The template replaces the current rule fields.", "Replace")) return;
            var copy = (JsonObject)template.DeepClone();
            if (selectedId != null) copy["id"] = selectedId;
            OpenRule(copy);
            dirty = true;
        });
        DockPanel.SetDock(applyTemplate, Dock.Right);
        templateRow.Children.Add(applyTemplate);
        templateRow.Children.Add(templates);
        editor.Children.Add(templateRow);
        editor.Children.Add(ViewUi.Text("Apply to", 13));
        var portRow = new WrapPanel { Margin = new Thickness(0, 7, 0, 10) };
        var selectedPorts = (rule["ports"] as JsonArray)?.Select(p => p!.GetValue<int>()).ToHashSet();
        int count = ViewUi.Int(client.State, "num_ports", 8);
        var ports = Enumerable.Range(1, count).Concat((client.State["sfp"] as JsonArray)?.OfType<JsonObject>().Select(p => ViewUi.Int(p, "port")) ?? Array.Empty<int>()).Distinct();
        foreach (int port in ports)
        {
            var box = new CheckBox { Content = ViewUi.PortLabel(port), IsChecked = selectedPorts?.Contains(port) ?? port <= count, Margin = new Thickness(0, 0, 16, 8) };
            box.Checked += Changed;
            box.Unchecked += Changed;
            portBoxes.Add((port, box));
            portRow.Children.Add(box);
        }
        editor.Children.Add(portRow);
        match = ViewUi.Choice(new[] { ("all", "All conditions"), ("any", "Any condition") }, ViewUi.Str(rule, "match", "all"));
        var matchField = ViewUi.Label("Match", match); matchField.Width = 210; matchField.HorizontalAlignment = HorizontalAlignment.Left;
        editor.Children.Add(matchField);
        conditionPanel = new StackPanel();
        editor.Children.Add(conditionPanel);
        foreach (var condition in (rule["conditions"] as JsonArray ?? new()).OfType<JsonObject>()) AddCondition(condition);
        var addCondition = ViewUi.Button("+ Add condition", () => { AddCondition(null); dirty = true; });
        addCondition.HorizontalAlignment = HorizontalAlignment.Left;
        editor.Children.Add(addCondition);
        editor.Children.Add(ViewUi.Separator());
        style = ViewUi.Choice(new[] { ("solid", "Solid color"), ("pulse", "Gentle pulse"), ("blink", "Blink"), ("flash", "Flash"), ("effect", "Keep effect") }, ViewUi.Str(rule["look"], "style", "solid"));
        var lookRow = new Grid();
        lookRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        lookRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        lookRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        var styleField = ViewUi.Label("When matched", style); styleField.Margin = new Thickness(0, 0, 12, 12);
        lookRow.Children.Add(styleField);
        color = new TextBox { Text = ViewUi.Str(rule["look"], "color", "#ff667e"), MaxLength = 7, MinWidth = 100 };
        var colorRow = ViewUi.ColorField(color);
        var colorField = ViewUi.Label("Color", colorRow); colorField.Margin = new Thickness(0, 0, 12, 12);
        Grid.SetColumn(colorField, 1); lookRow.Children.Add(colorField);
        double rate = ViewUi.Number(rule["look"], "rate", .25);
        cycle = new TextBox { Text = (1 / Math.Max(.05, rate)).ToString("0.##", CultureInfo.InvariantCulture) };
        var cycleField = ViewUi.Label("Cycle time (s)", cycle); cycleField.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetColumn(cycleField, 2); lookRow.Children.Add(cycleField);
        editor.Children.Add(lookRow);
        setBrightness = new CheckBox { Content = "Set brightness", IsChecked = rule["look"]?["brightness"] != null, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Center };
        brightness = new Slider { Minimum = 0, Maximum = 100, Value = ViewUi.Number(rule["look"], "brightness", 100), TickFrequency = 1, IsSnapToTickEnabled = true };
        var level = ViewUi.Text($"{brightness.Value:0}%", 13, true);
        var brightnessRow = new DockPanel();
        DockPanel.SetDock(level, Dock.Right);
        brightnessRow.Children.Add(level);
        brightnessRow.Children.Add(brightness);
        var levelRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(setBrightness, Dock.Left); levelRow.Children.Add(setBrightness); levelRow.Children.Add(brightnessRow);
        editor.Children.Add(levelRow);
        void UpdateLook()
        {
            string choice = ViewUi.Selected(style);
            colorRow.IsEnabled = choice != "effect";
            cycleField.Visibility = choice is "pulse" or "blink" or "flash" ? Visibility.Visible : Visibility.Collapsed;
            brightnessRow.IsEnabled = setBrightness.IsChecked == true;
        }
        style.SelectionChanged += (_, _) =>
        {
            UpdateLook();
            string choice = ViewUi.Selected(style);
            double minimum = choice == "pulse" ? 3.04 : choice == "flash" ? 2 : 1;
            if (double.TryParse(cycle.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds < minimum)
                cycle.Text = minimum.ToString("0.##", CultureInfo.InvariantCulture);
            dirty = true;
        };
        setBrightness.Checked += (_, _) => { UpdateLook(); dirty = true; };
        setBrightness.Unchecked += (_, _) => { UpdateLook(); dirty = true; };
        brightness.ValueChanged += (_, _) => { level.Text = $"{brightness.Value:0}%"; dirty = true; };
        name.TextChanged += (_, _) => dirty = true;
        color.TextChanged += (_, _) => dirty = true;
        cycle.TextChanged += (_, _) => dirty = true;
        enabled.Checked += Changed;
        enabled.Unchecked += Changed;
        match.SelectionChanged += (_, _) => dirty = true;
        UpdateLook();
        footer.Children.Add(ViewUi.Button("Save rule", SaveAsync));
        footer.Children.Add(ViewUi.Button("Cancel", async () => { dirty = false; await ReloadAsync(); }));
        if (selectedId != null) footer.Children.Add(ViewUi.Button("Delete rule", DeleteAsync));
        dirty = false;
        status.Text = "";
        Motion.Enter(editor);
    }

    private void Changed(object sender, RoutedEventArgs args) => dirty = true;

    private void AddCondition(JsonObject? spec)
    {
        var row = new ConditionEditor(fieldSpecs, spec, () => dirty = true);
        var container = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var remove = ViewUi.Button("−", () => { conditions.Remove(row); conditionPanel.Children.Remove(container); dirty = true; }, "Remove condition");
        DockPanel.SetDock(remove, Dock.Right);
        container.Children.Add(remove);
        container.Children.Add(row);
        conditions.Add(row);
        conditionPanel.Children.Add(container);
    }

    private async Task SaveAsync()
    {
        try
        {
            var title = name.Text.Trim();
            if (title.Length == 0) throw new InvalidOperationException("Enter a rule name.");
            var ports = portBoxes.Where(x => x.box.IsChecked == true).Select(x => x.port).ToArray();
            if (ports.Length == 0) throw new InvalidOperationException("Choose at least one port.");
            string chosenStyle = ViewUi.Selected(style);
            var look = new JsonObject { ["style"] = chosenStyle, ["color"] = ViewUi.ValidColor(color.Text) };
            if (chosenStyle is "pulse" or "blink" or "flash")
            {
                double seconds = ViewUi.ParseNumber(cycle.Text, "Cycle time");
                double minimum = chosenStyle == "pulse" ? 1 / .33 : chosenStyle == "flash" ? 2 : 1;
                if (seconds < minimum || seconds > 20) throw new InvalidOperationException($"Cycle time must be between {minimum:0.00} and 20 seconds.");
                look["rate"] = 1 / seconds;
            }
            look["brightness"] = setBrightness.IsChecked == true ? (int)Math.Round(brightness.Value) : null;
            var body = new JsonObject
            {
                ["name"] = title, ["enabled"] = enabled.IsChecked == true, ["match"] = ViewUi.Selected(match),
                ["ports"] = new JsonArray(ports.Select(p => JsonValue.Create(p)).ToArray()),
                ["conditions"] = new JsonArray(conditions.Select(c => (JsonNode)c.Read()).ToArray()), ["look"] = look
            };
            bool editing = selectedId != null;
            editor.IsEnabled = false;
            var response = await client.SendAsync(editing ? HttpMethod.Patch : HttpMethod.Post, editing ? "/api/rules/" + Uri.EscapeDataString(selectedId!) : "/api/rules", body);
            dirty = false;
            selectedId = response?["id"]?.GetValue<string>();
            await RefreshListAsync();
            if (response is JsonObject saved) OpenRule(saved);
            status.Text = "Rule saved.";
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
        finally { editor.IsEnabled = true; }
    }

    private async Task DeleteAsync()
    {
        if (selectedId == null || !ViewUi.Confirm(this, "Delete rule?", "This rule will be removed from your lighting setup.", "Delete")) return;
        try
        {
            await client.SendAsync(HttpMethod.Delete, "/api/rules/" + Uri.EscapeDataString(selectedId));
            dirty = false;
            selectedId = null;
            await ReloadAsync();
            status.Text = "Rule deleted.";
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    private async Task Move(int direction)
    {
        if (selectedId == null) return;
        var ids = ruleData.OfType<JsonObject>().Select(r => ViewUi.Str(r, "id")).ToList();
        int index = ids.IndexOf(selectedId), target = index + direction;
        if (index < 0 || target < 0 || target >= ids.Count) return;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        try { await client.SendAsync(HttpMethod.Put, "/api/rules/order", new { ids }); await RefreshListAsync(); status.Text = "Rule order updated."; }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    public async Task VerifyAsync()
    {
        ViewUi.RequireDemo(client);
        await ReloadAsync();
        var title = "Verification " + Guid.NewGuid().ToString("N")[..8];
        ViewUi.Click(this, "New rule");
        templates.SelectedItem = templates.Items.OfType<ComboBoxItem>().First(i => i.Content?.ToString() == "Anomaly");
        ViewUi.Click(this, "Use template");
        name.Text = title;
        if (!portBoxes.Any(p => p.port == 9)) throw new InvalidOperationException("SFP rule scope was not rendered.");
        foreach (var item in portBoxes) item.box.IsChecked = item.port == 9;
        UpdateLayout();
        var hexInput = ViewUi.Descendants<TextBox>(this).First(t => System.Windows.Automation.AutomationProperties.GetName(t) == "Hex color");
        hexInput.Text = "#72b8ff";
        hexInput.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(hexInput), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
        ViewUi.Click(this, "Save rule");
        await ViewUi.WaitAsync(() => status.Text == "Rule saved." && client.State["rules"] is JsonArray rows && rows.OfType<JsonObject>().Any(r => ViewUi.Str(r, "name") == title));
        var saved = (await client.GetAsync("/api/rules") as JsonArray)!.OfType<JsonObject>().Single(r => ViewUi.Str(r, "name") == title);
        if (saved["ports"]?.ToJsonString() != "[9]" || ViewUi.Str(saved["look"], "color") != "#72b8ff" || ViewUi.Str(saved["conditions"]?[0], "field") != "anomaly")
            throw new InvalidOperationException("Saved rule did not preserve its SFP target, color, and condition.");
        enabled.IsChecked = false;
        status.Text = "";
        ViewUi.Click(this, "Save rule");
        await ViewUi.WaitAsync(() => status.Text == "Rule saved.");
        saved = (await client.GetAsync("/api/rules") as JsonArray)!.OfType<JsonObject>().Single(r => ViewUi.Str(r, "name") == title);
        if (ViewUi.Bool(saved, "enabled", true)) throw new InvalidOperationException("Disabling the rule did not persist.");
        var id = ViewUi.Str(saved, "id");
        int before = ruleData.OfType<JsonObject>().Select(r => ViewUi.Str(r, "id")).ToList().IndexOf(id);
        if (before > 0)
        {
            ViewUi.Click(this, "↑");
            await ViewUi.WaitAsync(() => status.Text == "Rule order updated.");
            int after = (await client.GetAsync("/api/rules") as JsonArray)!.OfType<JsonObject>().Select(r => ViewUi.Str(r, "id")).ToList().IndexOf(id);
            if (after != before - 1) throw new InvalidOperationException("Rule priority did not change.");
        }
        ViewUi.Click(this, "Delete rule", "Delete");
        await ViewUi.WaitAsync(() => status.Text == "Rule deleted.");
        if ((await client.GetAsync("/api/rules") as JsonArray)!.OfType<JsonObject>().Any(r => ViewUi.Str(r, "id") == id)) throw new InvalidOperationException("Deleted rule remained in storage.");
        dirty = false;
    }

    private sealed class ConditionEditor : Grid
    {
        private readonly ComboBox field = new();
        private readonly ComboBox op = new();
        private readonly StackPanel valuePanel = new();
        private readonly JsonArray specs;
        private readonly Action changed;
        private Func<JsonNode?> readValue = () => JsonValue.Create(true);
        private bool initializing;

        internal ConditionEditor(JsonArray specs, JsonObject? condition, Action changed)
        {
            this.specs = specs;
            this.changed = changed;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(115) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            field.Margin = new Thickness(0, 0, 8, 0);
            op.Margin = new Thickness(0, 0, 8, 0);
            SetColumn(op, 1);
            SetColumn(valuePanel, 2);
            Children.Add(field); Children.Add(op); Children.Add(valuePanel);
            foreach (var spec in specs.OfType<JsonObject>()) field.Items.Add(new ComboBoxItem { Content = ViewUi.Str(spec, "label"), Tag = ViewUi.Str(spec, "key") });
            field.SelectionChanged += (_, _) => { if (!initializing) { Rebuild(null); changed(); } };
            op.SelectionChanged += (_, _) => { if (!initializing) { Values(null); changed(); } };
            initializing = true;
            ViewUi.Select(field, ViewUi.Str(condition, "field", "anomaly"));
            initializing = false;
            Rebuild(condition);
        }

        private JsonObject Spec => specs.OfType<JsonObject>().FirstOrDefault(s => ViewUi.Str(s, "key") == ViewUi.Selected(field)) ?? new();

        private void Rebuild(JsonObject? condition)
        {
            initializing = true;
            op.Items.Clear();
            foreach (var operation in Spec["operators"] as JsonArray ?? new())
            {
                string key = operation!.GetValue<string>();
                op.Items.Add(new ComboBoxItem { Content = OperatorName(key), Tag = key });
            }
            ViewUi.Select(op, ViewUi.Str(condition, "op", op.Items.OfType<ComboBoxItem>().FirstOrDefault()?.Tag?.ToString() ?? "is"));
            initializing = false;
            Values(condition?["value"]);
        }

        private static string OperatorName(string key) => key switch
        {
            "is" => "is", "is_not" => "is not", "in" => "is any of", "not_in" => "is none of", "gt" => "greater than", "ge" => "at least", "lt" => "less than", "le" => "at most", "eq" => "equals", "ne" => "not equal", "between" => "between", _ => key
        };

        private void Values(JsonNode? value)
        {
            valuePanel.Children.Clear();
            string type = ViewUi.Str(Spec, "type"), operation = ViewUi.Selected(op);
            if (type == "bool")
            {
                var box = ViewUi.Choice(new[] { ("true", "Yes"), ("false", "No") }, value?.GetValue<bool>() == false ? "false" : "true");
                box.SelectionChanged += (_, _) => changed();
                valuePanel.Children.Add(box);
                readValue = () => JsonValue.Create(ViewUi.Selected(box) == "true");
            }
            else if (type is "weekday" || (type == "choice" && operation is "in" or "not_in"))
            {
                var wrap = new WrapPanel();
                var boxes = new List<(string key, CheckBox box)>();
                var selected = (value as JsonArray)?.Select(v => v!.GetValue<string>()).ToHashSet() ?? new();
                foreach (var choice in Spec["choices"] as JsonArray ?? new())
                {
                    string key = choice!.GetValue<string>();
                    var box = new CheckBox { Content = DisplayChoice(key), IsChecked = selected.Contains(key), Margin = new Thickness(0, 6, 12, 6) };
                    box.Checked += (_, _) => changed(); box.Unchecked += (_, _) => changed();
                    boxes.Add((key, box)); wrap.Children.Add(box);
                }
                valuePanel.Children.Add(wrap);
                readValue = () =>
                {
                    var selectedValues = boxes.Where(b => b.box.IsChecked == true).Select(b => JsonValue.Create(b.key)).ToArray();
                    if (selectedValues.Length == 0) throw new InvalidOperationException("Choose at least one condition value.");
                    return new JsonArray(selectedValues);
                };
            }
            else if (type == "choice")
            {
                var choices = (Spec["choices"] as JsonArray ?? new()).Select(n => n!.GetValue<string>()).ToArray();
                var box = ViewUi.Choice(choices.Select(c => (c, DisplayChoice(c))), value?.GetValue<string>() ?? choices.FirstOrDefault() ?? "");
                box.SelectionChanged += (_, _) => changed(); valuePanel.Children.Add(box);
                readValue = () => JsonValue.Create(ViewUi.Selected(box));
            }
            else if (type == "time" || operation == "between")
            {
                var pair = new Grid(); pair.ColumnDefinitions.Add(new()); pair.ColumnDefinitions.Add(new());
                var first = new TextBox { Text = (value as JsonArray)?[0]?.ToString() ?? (type == "time" ? "22:00" : "0"), Margin = new Thickness(0, 0, 6, 0) };
                var second = new TextBox { Text = (value as JsonArray)?[1]?.ToString() ?? (type == "time" ? "07:00" : "100") };
                first.TextChanged += (_, _) => changed(); second.TextChanged += (_, _) => changed();
                SetColumn(second, 1); pair.Children.Add(first); pair.Children.Add(second); valuePanel.Children.Add(pair);
                readValue = () => type == "time" ? new JsonArray(Clock(first.Text), Clock(second.Text)) : new JsonArray(ViewUi.ParseNumber(first.Text, "Lower value"), ViewUi.ParseNumber(second.Text, "Upper value"));
            }
            else
            {
                var input = new TextBox { Text = value?.ToString() ?? "0" };
                input.TextChanged += (_, _) => changed(); valuePanel.Children.Add(input);
                string unit = ViewUi.Str(Spec, "unit");
                if (unit.Length > 0) valuePanel.Children.Add(ViewUi.Text(unit, 10, true));
                readValue = () => JsonValue.Create(ViewUi.ParseNumber(input.Text, "Condition value"));
            }
        }

        private static string DisplayChoice(string value) => value.StartsWith("poe", StringComparison.Ordinal) ? "PoE" + value[3..] : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
        private static string Clock(string value)
        {
            if (!TimeOnly.TryParseExact(value.Trim(), new[] { "H:mm", "HH:mm" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new InvalidOperationException("Enter time as HH:mm, for example 22:00.");
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        internal JsonObject Read() => new() { ["field"] = ViewUi.Selected(field), ["op"] = ViewUi.Selected(op), ["value"] = readValue() };
    }
}

internal static class ViewUi
{
    internal static string Str(JsonNode? node, string key, string fallback = "") => node?[key]?.ToString() ?? fallback;
    internal static bool Bool(JsonNode? node, string key, bool fallback = false) => node?[key] is JsonValue value && value.TryGetValue<bool>(out bool result) ? result : fallback;
    internal static int Int(JsonNode? node, string key, int fallback = 0) => (int)Number(node, key, fallback);
    internal static double Number(JsonNode? node, string key, double fallback = 0) => double.TryParse(node?[key]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : fallback;
    internal static string PortLabel(int port) => port > 8 ? $"SFP {port}" : $"Port {port}";
    internal static TextBlock Text(string text, double size = 13, bool muted = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "MutedBrush" : "TextBrush");
        return block;
    }
    internal static Brush Theme(string key) => (Brush)Application.Current.FindResource(key);
    internal static TextBlock Status()
    {
        var status = Text("", 12, true);
        status.Margin = new Thickness(0, 14, 0, 0);
        return status;
    }
    internal static StackPanel Header(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 22) };
        var heading = Text(title, 25); heading.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(heading); panel.Children.Add(Text(subtitle, 13, true)); return panel;
    }
    internal static StackPanel Label(string text, UIElement input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var label = Text(text, 12, true); label.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(label); panel.Children.Add(input);
        if (input is DependencyObject element) System.Windows.Automation.AutomationProperties.SetName(element, text);
        return panel;
    }
    internal static Button Button(string label, Action action, string? help = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 7, 0), ToolTip = help };
        if (label is "Save rule" or "Save groups" or "Save current lighting" or "Apply scene") button.Style = Application.Current.TryFindResource("PrimaryButton") as Style;
        if (help != null) System.Windows.Automation.AutomationProperties.SetName(button, help);
        button.Click += (_, _) => action(); return button;
    }
    internal static Button Button(string label, Func<Task> action, string? help = null)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 7, 0), ToolTip = help };
        if (label is "Save rule" or "Save groups" or "Save current lighting" or "Apply scene") button.Style = Application.Current.TryFindResource("PrimaryButton") as Style;
        if (help != null) System.Windows.Automation.AutomationProperties.SetName(button, help);
        button.Click += async (_, _) => { button.IsEnabled = false; try { await action(); } finally { button.IsEnabled = true; } }; return button;
    }
    internal static Border Separator() => new() { Height = 1, Background = Theme("BorderBrush"), Margin = new Thickness(0, 17, 0, 18) };
    internal static ComboBox Choice(IEnumerable<(string key, string label)> choices, string selected)
    {
        var box = new ComboBox();
        foreach (var (key, label) in choices) box.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        Select(box, selected); return box;
    }
    internal static string Selected(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
    internal static void Select(ComboBox box, string key) => box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == key) ?? box.Items.OfType<ComboBoxItem>().FirstOrDefault();
    internal static double ParseNumber(string text, string label)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new InvalidOperationException($"{label} must be a number.");
        return number;
    }
    internal static string ValidColor(string text)
    {
        text = text.Trim();
        if (text.Length != 7 || text[0] != '#' || !text.Skip(1).All(Uri.IsHexDigit)) throw new InvalidOperationException("Enter a six-digit color, such as #5588FF.");
        return text.ToLowerInvariant();
    }
    internal static Panel ColorField(TextBox hex)
    {
        var panel = new StackPanel();
        var picker = new ColorWell { Hex = hex.Text, Brightness = 100, ShowBrightness = false };
        picker.Changed += () => hex.Text = picker.Hex;
        panel.Children.Add(picker);
        return panel;
    }
    internal static void Error(TextBlock status, Exception error) => status.Text = error is InvalidOperationException or EngineApiException ? error.Message : "The change could not be completed. Please try again.";
    internal static bool Confirm(DependencyObject context, string title, string body, string accept)
    {
        var window = new Window { Title = title, Width = 410, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(context), ShowInTaskbar = false, Background = Theme("SurfaceBrush"), Foreground = Theme("TextBrush") };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Header(title, body));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("Cancel", () => window.DialogResult = false); cancel.IsCancel = true;
        buttons.Children.Add(cancel); buttons.Children.Add(Button(accept, () => window.DialogResult = true));
        panel.Children.Add(buttons); DialogChrome(window, panel, title); return window.ShowDialog() == true;
    }

    internal static void DialogChrome(Window window, UIElement body, string title)
    {
        window.FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        window.FontSize = 13;
        window.WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 38, ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 5), GlassFrameThickness = new Thickness(0) });
        var layout = new DockPanel();
        var caption = new DockPanel { Height = 38, Background = Theme("PanelBrush") };
        var close = Button("×", () => window.Close(), "Close dialog"); close.Padding = new Thickness(12, 0, 12, 0); close.Margin = new Thickness(0); close.IsCancel = true;
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        DockPanel.SetDock(close, Dock.Right); caption.Children.Add(close);
        var text = Text(title, 12, true); text.Margin = new Thickness(16, 0, 0, 0); caption.Children.Add(text);
        DockPanel.SetDock(caption, Dock.Top); layout.Children.Add(caption); layout.Children.Add(body);
        window.Content = new Border { BorderBrush = Theme("BorderBrush"), BorderThickness = new Thickness(1), Child = layout };
        window.Loaded += (_, _) => Motion.Enter(layout);
    }

    internal static void RequireDemo(EngineClient client)
    {
        if (!Bool(client.Session, "simulated")) throw new InvalidOperationException("Workflow verification requires the isolated simulator.");
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject item) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(item); index++)
        {
            var child = VisualTreeHelper.GetChild(item, index);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    internal static void Click(FrameworkElement root, string label, string? confirmation = null)
    {
        root.UpdateLayout();
        var button = Descendants<Button>(root).FirstOrDefault(b => b.Content?.ToString() == label && b.IsEnabled)
            ?? throw new InvalidOperationException("Control not available: " + label);
        if (confirmation != null)
        {
            _ = root.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                var dialog = Application.Current.Windows.OfType<Window>().LastOrDefault(w => w.Owner != null && w.IsVisible);
                if (dialog == null) throw new InvalidOperationException("Confirmation dialog did not open.");
                Click(dialog, confirmation);
            }));
        }
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    }

    internal static async Task WaitAsync(Func<bool> finished)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!finished())
        {
            if (DateTime.UtcNow >= deadline) throw new InvalidOperationException("The workflow did not finish within its verification deadline.");
            await Task.Delay(35);
        }
    }
}
