using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Layout;
using EtherlightingEvolved.Native.Ui;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

internal sealed partial class AutomationPage
{
    Control RuleEditor()
    {
        var editor = U.Column(12);
        var name = U.Input(draft.Str("name"), changed: value => { if (draft.Str("name") == value) return; draft["name"] = value; Changed(); });
        name.MaxLength = 60;
        Avalonia.Automation.AutomationProperties.SetName(name, "Rule name");
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var enabled = U.Check("Enabled", draft.Bool("enabled") != false, value => { if (draft.Bool("enabled") != value) { draft["enabled"] = value; Changed(); } });
        enabled.Margin = new Avalonia.Thickness(16, 19, 0, 0);
        Grid.SetColumn(enabled, 1);
        heading.Children.Add(U.Field("Rule name", name)); heading.Children.Add(enabled);
        var sections = U.Wrap(5);
        foreach (var label in new[] { "When", "Ports", "Appearance" })
            sections.Children.Add(U.Button(label, section == label ? "selected" : "quiet", () => { section = label; Draw(); }));
        var when = When(); var ports = Scope(); var look = Appearance();
        editor.Children.Add(when); editor.Children.Add(ports); editor.Children.Add(look);
        summary = U.P("", "muted");
        summary.Margin = new Avalonia.Thickness(0, 4, 0, 5);
        apply = ActionButton(reordered ? "Apply rule and priority" : "Apply rule", SaveRule, "primary");
        var actions = U.Wrap(8, ActionButton("Test rules", () => TestRules(Candidate(selected ?? "preview-rule"))), apply,
            U.Button("Cancel rule changes", null, () => { edited = false; ResetRule(); Draw(); }));
        if (selected != null)
        {
            int idx = rules.IndexOf(rules.Objects().First(r => r.Str("id") == selected));
            foreach (var (delta, label) in new[] { (-1, "Move up"), (1, "Move down") })
            {
                int target = idx + delta;
                var move = U.Button(label, "quiet", () =>
                {
                    var rule = rules[idx]; rules.RemoveAt(idx); rules.Insert(target, rule);
                    reordered = !JsonNode.DeepEquals(rules, baseline.At("scene.rules"));
                    Draw();
                });
                move.IsEnabled = target >= 0 && target < rules.Count;
                actions.Children.Add(move);
            }
            actions.Children.Add(ActionButton("Delete rule", DeleteRule, "quiet danger"));
        }
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        head.Children.Add(heading); head.Children.Add(sections);
        var layout = Editor(editor, U.Column(2, summary, actions), head);
        void Resize()
        {
            var desktop = TopLevel.GetTopLevel(this)?.ClientSize ?? new Avalonia.Size(1366, 768);
            bool rack = desktop.Height <= 500 && desktop.Width >= 900;
            bool compact = rack || desktop.Width <= 700;
            sections.IsVisible = compact;
            sections.Margin = rack ? new Avalonia.Thickness(14, 0, 0, 0) : new Avalonia.Thickness(0, 6, 0, 0);
            Grid.SetColumn(sections, rack ? 1 : 0); Grid.SetRow(sections, rack ? 0 : 1);
            when.IsVisible = !compact || section == "When";
            ports.IsVisible = !compact || section == "Ports";
            look.IsVisible = !compact || section == "Appearance";
        }
        layout.SizeChanged += (_, _) => Resize();
        Resize(); UpdateSummary();
        return layout;
    }

    static JsonObject Initial(JsonObject spec)
    {
        string? type = spec.Str("type");
        return new JsonObject
        {
            ["field"] = spec["key"].Copy(),
            ["op"] = type == "number" ? "ge" : spec.Arr("operators").FirstOrDefault().Copy(),
            ["value"] = type switch
            {
                "number" => JsonValue.Create(100),
                "bool" => JsonValue.Create(true),
                "time" => Strings(["22:00", "07:00"]),
                "weekday" => Strings(["mon", "tue", "wed", "thu", "fri"]),
                _ => spec.Arr("choices").FirstOrDefault().Copy()
            }
        };
    }
    Control When()
    {
        var view = U.Column(10, U.Text("When", "h3 accent"), U.P("Choose the conditions that trigger this rule.", "muted"));
        var conditions = draft.Arr("conditions");
        if (conditions.Count == 0) view.Children.Add(U.P("Add a condition or choose a template.", "muted"));
        view.Children.Add(U.Field("Match", U.Select([("all", "All conditions"), ("any", "Any condition")], draft.Str("match"), value => { if (draft.Str("match") != value) { draft["match"] = value; Changed(); } })));
        foreach (var condition in conditions.Objects()) view.Children.Add(Condition(condition, conditions));
        var add = U.Button("Add condition", "quiet", () =>
        {
            var spec = specs.Objects().FirstOrDefault(s => s.Str("key") == "anomaly") ?? specs.Objects().First();
            conditions.Add(Initial(spec)); Changed(); Draw();
        });
        add.IsEnabled = specs.Count > 0; view.Children.Add(add);
        var options = new List<(string, string)> { ("", "Choose a template") };
        options.AddRange(templates.Objects().Select((t, i) => (i.ToString(), t.Str("name")!)));
        string template = "";
        var select = U.Select(options, "", v => template = v);
        view.Children.Add(U.Wrap(8, select, ActionButton("Use template", async () =>
        {
            if (!int.TryParse(template, out int i)) return;
            if (draft.Arr("conditions").Count > 0 && !await Shell.Overlay.Confirm("Use template?", "Replace the conditions and appearance in this rule?", "Use template")) return;
            draft = SecondaryModel.TemplateForRule(templates[i], draft, Ports.Copper); Changed(); Draw();
        })));
        if (conditions.Objects().Any(c => c.Str("field") is "time" or "weekday"))
            view.Children.Add(U.P($"Schedule time: {ScheduleClock.Label(Workspace.State)}. A time range can continue past midnight.", "muted"));
        return view;
    }

    Control Condition(JsonObject condition, JsonArray conditions)
    {
        var row = U.Column(8);
        var options = specs.Objects().Select(s => (s.Str("key")!, s.Str("label")!)).ToList();
        bool available = SecondaryModel.ConditionAvailable(condition, specs);
        if (!available) { row.Children.Add(U.P("This condition is unavailable. Choose a replacement or remove it.", "muted")); options.Insert(0, ("", "Choose a condition")); }
        var field = U.Select(options, available ? condition.Str("field") : "", key =>
        {
            if (condition.Str("field") == key && available) return;
            var spec = specs.Objects().FirstOrDefault(s => s.Str("key") == key);
            if (spec == null) return;
            conditions[conditions.IndexOf(condition)] = Initial(spec); Changed(); Draw();
        });
        var toolbar = U.Wrap(6, U.Field("Condition", field));
        if (available)
        {
            var spec = specs.Objects().First(s => s.Str("key") == condition.Str("field"));
            var op = U.Select(Values(spec["operators"]).Select(v => (v, Rules.OperatorLabel(v) ?? v)).ToList(), condition.Str("op"), key =>
            {
                if (condition.Str("op") == key) return;
                var value = condition["value"].Copy();
                if (key == "between" && value is not JsonArray) value = new JsonArray(value, (value.Num() ?? 0) + 100);
                else if (key is "in" or "not_in" && value is not JsonArray) value = new JsonArray(value);
                else if (key is not ("between" or "in" or "not_in") && value is JsonArray array) value = array.FirstOrDefault().Copy();
                condition["op"] = key; condition["value"] = value; Changed(); Draw();
            });
            toolbar.Children.Add(U.Field("Operator", op));
            toolbar.Children.Add(ConditionValue(condition, spec));
        }
        toolbar.Children.Add(U.Button("Remove", "quiet", () => { conditions.Remove(condition); Changed(); Draw(); }));
        row.Children.Add(toolbar);
        return row;
    }

    Control ConditionValue(JsonObject c, JsonObject spec)
    {
        string? type = spec.Str("type"), op = c.Str("op");
        void Set(JsonNode? value) { if (!JsonNode.DeepEquals(c["value"], value)) { c["value"] = value; Changed(); } }
        var choices = Values(spec["choices"]).Select(v => (v, v.StartsWith("poe", StringComparison.Ordinal) ? "PoE" + v[3..] : Title(v.Replace('_', ' ')))).ToList();
        if (type == "bool")
            return U.Field("Value", U.Select([("true", "Yes"), ("false", "No")], c.Bool("value") == true ? "true" : "false", v => Set(v == "true")));
        if (type == "weekday" || (type == "choice" && op is "in" or "not_in"))
        {
            var list = U.Wrap(5); var selectedValues = Values(c["value"]).ToHashSet();
            foreach (var (value, label) in choices)
                list.Children.Add(U.Check(label, selectedValues.Contains(value), on =>
                {
                    if (on) selectedValues.Add(value); else selectedValues.Remove(value);
                    Set(Strings(choices.Select(p => p.v).Where(selectedValues.Contains)));
                }));
            return list;
        }
        if (type == "choice") return U.Field("Value", U.Select(choices, c.Str("value"), v => Set(v)));
        if (type == "time" || op == "between")
        {
            var pair = c.Arr("value");
            var values = new[] { pair.ElementAtOrDefault(0)?.ToString() ?? "", pair.ElementAtOrDefault(1)?.ToString() ?? "" };
            var view = U.Wrap(5);
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                var box = U.Input(values[i], type == "time" ? "HH:mm" : "0", v =>
                {
                    if (v == values[index]) return;
                    values[index] = v;
                    Set(type == "time" ? Strings(values) : new JsonArray(values.Select(Number).ToArray()));
                });
                box.MinWidth = 74;
                view.Children.Add(U.Field(i == 0 ? "Start" : "End", box));
            }
            return view;
        }
        return U.Field(spec.Str("unit") is { } unit ? $"Value ({unit})" : "Value",
            U.Input(c["value"]?.ToString(), changed: v => Set(Number(v))));
    }
    static JsonNode? Number(string value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? JsonValue.Create(number) : null;

    Control Scope()
    {
        var ports = Ports;
        var all = ports.Copper.Concat(ports.Sfp).ToList();
        var selectedPorts = (draft["ports"] == null ? ports.Copper : PortTopology.PortIds(draft["ports"])).ToHashSet();
        void Set(IEnumerable<int> values) { draft["ports"] = Js.NumberArray(values.Order()); Changed(); }
        var bar = U.Wrap(6);
        foreach (var (label, scope) in new (string, IEnumerable<int>)[] { ("All ports", all), ("Ethernet", ports.Copper), ("SFP", ports.Sfp), ("None", Array.Empty<int>()) })
        {
            if (!scope.Any() && label != "None") continue;
            bar.Children.Add(U.Button(label, "quiet", () => { Set(scope); Draw(); }));
        }
        var boxes = U.Wrap(8);
        foreach (var id in all)
            boxes.Children.Add(U.Check((ports.Sfp.Contains(id) ? "SFP " : "Port ") + id, selectedPorts.Contains(id), on =>
            { if (on) selectedPorts.Add(id); else selectedPorts.Remove(id); Set(selectedPorts); }));
        return U.Column(10, U.Text("Ports", "h3 accent"), bar, boxes);
    }

    Control Appearance()
    {
        var look = draft.Obj("look");
        var view = U.Column(10, U.Text("Appearance", "h3 accent"));
        var style = look.Str("style") ?? "solid";
        view.Children.Add(U.Field("Appearance", U.Select(RuleStyles, style, key =>
        {
            if (key == style) return;
            look["style"] = key; look["rate"] = Rules.RuleRate(key, look.Num("rate")); Changed(); Draw();
        })));
        if (style != "effect")
        {
            var current = look.Str("color") ?? "#ff667e";
            var hex = U.Input(current);
            hex.MaxLength = 7;
            var sample = new Border { Width = 25, Height = 23, CornerRadius = new Avalonia.CornerRadius(3), Background = U.Brush(current) };
            var swatch = U.Button("", "quiet", () =>
                ColorLibraryDialog.Open(Shell, "Rule color", hex.Text ?? current, value => hex.Text = value));
            swatch.Content = sample;
            swatch.Padding = new Avalonia.Thickness(4);
            Avalonia.Automation.AutomationProperties.SetName(swatch, "Rule color");
            Avalonia.Automation.AutomationProperties.SetName(hex, "Hex color");
            hex.PropertyChanged += (_, change) =>
            {
                if (change.Property != TextBox.TextProperty) return;
                var value = hex.Text ?? "";
                look["color"] = value; Changed();
                if (ColorMath.NormalizeHex(value) is { } color) sample.Background = U.Brush(color);
            };
            view.Children.Add(U.Field("Color", U.Row(8, swatch, hex)));
        }
        if (Rules.RuleRateLimits.TryGetValue(style, out var limit))
        {
            var rate = Rules.RuleRate(style, look.Num("rate"));
            var period = new NumericUpDown { Minimum = (decimal)(1 / limit.Max), Maximum = (decimal)(1 / limit.Min), Increment = .1m, Value = (decimal)(1 / rate), FormatString = "0.##" };
            period.ValueChanged += (_, _) => { if (period.Value is > 0) { look["rate"] = 1 / (double)period.Value.Value; Changed(); } };
            view.Children.Add(U.Field("Cycle length (seconds)", period));
        }
        bool custom = look.Num("brightness") != null;
        var level = U.Range(0, 100, 1, look.Num("brightness") ?? Workspace.State.Num("brightness") ?? 100);
        var label = U.Text($"{level.Value:0}%");
        level.IsEnabled = custom;
        level.ValueChanged += (_, _) => { if (level.IsEnabled) { look["brightness"] = (int)level.Value; label.Text = $"{level.Value:0}%"; Changed(); } };
        view.Children.Add(U.Check("Set brightness", custom, on => { level.IsEnabled = on; look["brightness"] = on ? (int)level.Value : null; Changed(); }));
        view.Children.Add(U.Row(10, level, label)); level.Width = 180;
        return view;
    }
}
