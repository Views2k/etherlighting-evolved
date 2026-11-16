using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

/// <summary>Port of drawEditor and conditionEditor (secondary.mjs:139-201, 394-551).</summary>
internal sealed partial class AutomationPage
{
    /// <summary>The cycle field's lowest value per animated style (updateStyle).</summary>
    static readonly Dictionary<string, double> CycleMinimums = new(StringComparer.Ordinal) { ["pulse"] = 3.04, ["flash"] = 2, ["blink"] = 1 };
    bool sectionNavShown;
    string cycleText = "", cycleInitial = "", colorText = "";

    static bool IsHex(string? value) => value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit);

    sealed record RuleSection(Border Frame, TextBlock Heading, TextBlock? Help);

    /// <summary>section.automation-section: heading, optional help text, contents 14px apart, a hairline above all but the first.</summary>
    static RuleSection Section(string title, string? description, bool first, out StackPanel content)
    {
        content = new StackPanel { Spacing = 14 };
        var heading = SectionHeading(title);
        content.Children.Add(heading);
        TextBlock? help = null;
        if (description != null)
        {
            help = HelpText(description);
            help.Margin = new Thickness(0, 5, 0, 0);
            content.Children.Add(help);
        }
        var frame = new Border { Child = content, BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0), Padding = new Thickness(0, first ? 0 : 18, 0, 0) }
            .Bind(Border.BorderBrushProperty, "line");
        return new RuleSection(frame, heading, help);
    }

    Control RuleEditor()
    {
        var look = draft.Obj("look");
        var name = Key(U.Input(draft.Str("name"), changed: value =>
        {
            if (draft.Str("name") == value) return;
            draft["name"] = value;
            Changed();
        }), "rule-name");
        name.MaxLength = 60;
        AutomationProperties.SetName(name, "Rule name");
        var enabled = U.Check("Enabled", draft["enabled"]?.GetValueKind() != System.Text.Json.JsonValueKind.False, value =>
        {
            if (draft.Bool("enabled") == value) return;
            draft["enabled"] = value;
            Changed();
        });
        enabled.VerticalAlignment = VerticalAlignment.Bottom;
        enabled.Margin = new Thickness(16, 0, 0, 1);
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(U.Field("Rule name", name));
        Grid.SetColumn(enabled, 1);
        heading.Children.Add(enabled);

        // nav.rule-section-nav, shown in narrow and rack windows
        var navButtons = new UniformGrid { Rows = 1 };
        foreach (var label in new[] { "When", "Ports", "Appearance" })
        {
            var button = PlainButton(label, () => { section = label; Draw(); }, "quiet");
            button.Classes.Set("selected", section == label);
            button.BorderThickness = new Thickness(0);
            button.Margin = new Thickness(1.5, 0);
            AutomationProperties.SetHelpText(button, section == label ? "Selected" : "");
            navButtons.Children.Add(button);
        }
        var nav = new Border { Child = navButtons, Padding = new Thickness(3), CornerRadius = new CornerRadius(7) }.Bind(Border.BackgroundProperty, "editor-tab-surface");
        AutomationProperties.SetName(nav, "Rule sections");

        var when = When();
        var scope = Scope();
        var appearance = Appearance(look);
        var body = new StackPanel { Spacing = 17, Margin = new Thickness(0, 0, 18, 20) };
        body.Children.Add(when.Frame); body.Children.Add(scope.Frame); body.Children.Add(appearance.Frame);
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        ruleScroll = scroll;

        summary = U.Text("", null, true);
        summary.FontSize = 12;
        summary.LineHeight = 16.8;
        summary.Bind(TextBlock.ForegroundProperty, "rule-summary-text");
        var summaryBox = new Border { Child = summary, Padding = new Thickness(12, 8), CornerRadius = new CornerRadius(7) }.Bind(Border.BackgroundProperty, "rule-summary-surface");

        apply = ActionButton(reordered ? "Apply rule and priority" : "Apply rule", SaveRule, "primary");
        var test = U.Button("Test rules", "quiet", () => TestRules(Candidate(selected ?? "preview-rule")));
        var actions = new FlexRow { Gap = 8 };
        actions.Children.Add(test);
        actions.Children.Add(apply);
        actions.Children.Add(PlainButton("Cancel rule changes", () =>
        {
            edited = false;
            ResetRule();
            Draw();
            RequestFocus("rule-name");
        }, null));
        if (selected != null && rules.Objects().FirstOrDefault(r => r.Str("id") == selected) is { } current)
        {
            int index = rules.IndexOf(current);
            var priority = U.Row(8);
            foreach (var (delta, label) in new[] { (-1, "Move up"), (1, "Move down") })
            {
                int target = index + delta;
                var move = PlainButton(label, () =>
                {
                    if (target < 0 || target >= rules.Count) return;
                    var a = rules[index]!.DeepClone(); var b = rules[target]!.DeepClone();
                    rules[index] = b; rules[target] = a;
                    reordered = true;
                    Draw();
                }, "quiet");
                move.IsEnabled = target >= 0 && target < rules.Count;
                priority.Children.Add(move);
            }
            actions.Children.Add(FlexRow.Push(priority));
            actions.Children.Add(FlexRow.Push(ActionButton("Delete rule", DeleteRule, "quiet danger")));
        }
        var footer = new Border { Child = actions, Padding = new Thickness(0, 14), BorderThickness = new Thickness(0, 1, 0, 0) }.Bind(Border.BorderBrushProperty, "line");

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 0, 0, 12) };
        head.Children.Add(heading);
        head.Children.Add(nav);
        var form = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto") };
        Grid.SetRow(scroll, 1); Grid.SetRow(summaryBox, 2); Grid.SetRow(footer, 3);
        form.Children.Add(head); form.Children.Add(scroll); form.Children.Add(summaryBox); form.Children.Add(footer);

        void Resize()
        {
            var window = TopLevel.GetTopLevel(this)?.ClientSize ?? new Size(1366, 768);
            bool rack = window.Height <= 500 && window.Width >= 900;
            bool compact = rack || window.Width <= 700;
            sectionNavShown = compact;
            nav.IsVisible = compact;
            nav.Margin = rack ? new Thickness(16, 0, 0, 2) : new Thickness(0, 8, 0, 0);
            Grid.SetColumn(nav, rack ? 1 : 0); Grid.SetRow(nav, rack ? 0 : 1);
            head.Margin = new Thickness(0, 0, 0, rack ? 4 : 12);
            foreach (var (part, key) in new[] { (when, "When"), (scope, "Ports"), (appearance, "Appearance") })
            {
                part.Frame.IsVisible = !compact || section == key;
                part.Heading.IsVisible = !compact;
                if (part.Help != null) part.Help.IsVisible = !compact;
                bool first = key == "When";
                part.Frame.BorderThickness = new Thickness(0, compact || first ? 0 : 1, 0, 0);
                part.Frame.Padding = new Thickness(0, compact || first ? 0 : 18, 0, 0);
            }
            summaryBox.Padding = compact ? new Thickness(0, 6, 0, 0) : new Thickness(12, 8);
            summaryBox.CornerRadius = compact ? default : new CornerRadius(7);
            summaryBox.BorderThickness = new Thickness(0, compact ? 1 : 0, 0, 0);
            summaryBox.Bind(Border.BorderBrushProperty, "line");
            if (compact) summaryBox.Background = Brushes.Transparent; else summaryBox.Bind(Border.BackgroundProperty, "rule-summary-surface");
            footer.Padding = new Thickness(0, rack ? 5 : 14);
            foreach (var row in conditionRows) LayoutCondition(row, rack ? 0 : window.Width <= 800 ? 2 : window.Width <= 1050 ? 1 : 0);
        }
        form.SizeChanged += (_, _) => Resize();
        Resize();
        UpdateSummary();
        var offset = ruleOffset;
        Dispatcher.UIThread.Post(() => { if (ruleScroll == scroll) scroll.Offset = offset; }, DispatcherPriority.Loaded);
        return form;
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
                "bool" => JsonValue.Create(true),
                "number" => JsonValue.Create(100),
                "time" => Strings(["22:00", "07:00"]),
                "weekday" => Strings(["mon", "tue", "wed", "thu", "fri"]),
                _ => spec.Arr("choices").FirstOrDefault().Copy()
            }
        };
    }

    RuleSection When()
    {
        var part = Section("When", "Choose the conditions that trigger this rule.", true, out var view);
        var conditions = draft.Arr("conditions");
        if (conditions.Count == 0)
        {
            var empty = HelpText("Add a condition or choose a template.");
            empty.Margin = new Thickness(0, 5, 0, 0);
            view.Children.Add(empty);
        }
        conditionRows.Clear();
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(0, 10) };
        for (int i = 0; i < conditions.Count; i++)
            if (conditions[i] is JsonObject condition) list.Children.Add(Condition(condition, i));
        view.Children.Add(list);
        var match = U.Select([("all", "All conditions"), ("any", "Any condition")], draft.Str("match") ?? "all", value =>
        {
            if (draft.Str("match") == value) return;
            draft["match"] = value;
            Changed();
        });
        AutomationProperties.SetName(match, "Match");
        view.Children.Add(U.Field("Match", match));
        var add = Key(PlainButton("Add condition", () =>
        {
            var spec = specs.Objects().FirstOrDefault(s => s.Str("key") == "anomaly") ?? specs.Objects().First();
            conditions.Add(Initial(spec));
            edited = true;
            Draw();
            RequestFocus($"condition-{conditions.Count - 1}-property");
        }), "condition-add");
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        add.IsEnabled = specs.Count > 0;
        view.Children.Add(add);
        // div.row.template-row: the select fills the row up to 530px, then the default-style button.
        var options = new List<(string, string)> { ("", "Choose a template") };
        options.AddRange(templates.Objects().Select((t, i) => (i.ToString(CultureInfo.InvariantCulture), t.Str("name") ?? "")));
        string chosen = "";
        var template = U.Select(options, "", value => chosen = value);
        AutomationProperties.SetName(template, "Rule template");
        var use = ActionButton("Use template", () =>
        {
            if (!int.TryParse(chosen, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= templates.Count) return Task.CompletedTask;
            draft = SecondaryModel.TemplateForRule(templates[index], draft, Ports.Copper);
            if (draft["look"] is not JsonObject) draft["look"] = new JsonObject();
            edited = true;
            Draw();
            RequestFocus("condition-0-property");
            return Task.CompletedTask;
        }, null);
        use.Margin = new Thickness(10, 0, 0, 0);
        var row = new DockPanel();
        DockPanel.SetDock(use, Dock.Right);
        row.Children.Add(use);
        row.Children.Add(template);
        view.Children.Add(new EditorColumn { Limit = 530, Child = row });
        if (conditions.Objects().Any(c => c.Str("field") is "time" or "weekday"))
        {
            scheduleHint = HelpText("");
            scheduleHint.Margin = new Thickness(0, 5, 0, 0);
            view.Children.Add(scheduleHint);
            UpdateRuleOutput(Workspace.State);
        }
        return part;
    }

    readonly List<Grid> conditionRows = [];

    /// <summary>
    /// div.condition-row: property, operator, value and Remove in the browser's grid columns. Below 1050px the value
    /// moves to a second line and Remove spans both (style.css:724-726); below 800px the first two columns share the row.
    /// </summary>
    static void LayoutCondition(Grid row, int mode)
    {
        row.ColumnDefinitions.Clear();
        if (mode == 0)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 110 });
            row.ColumnDefinitions.Add(new ColumnDefinition(.8, GridUnitType.Star) { MinWidth = 100 });
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 100 });
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        else
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = mode == 2 ? 0 : 100 });
            row.ColumnDefinitions.Add(new ColumnDefinition(mode == 2 ? 1 : .8, GridUnitType.Star) { MinWidth = mode == 2 ? 0 : 90 });
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        }
        var parts = row.Children.ToList();
        bool available = parts.Count == 4;
        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            Grid.SetRowSpan(part, 1); Grid.SetColumnSpan(part, 1);
            if (mode == 0) { Grid.SetColumn(part, i); Grid.SetRow(part, 0); continue; }
            bool remove = i == parts.Count - 1;
            if (remove) { Grid.SetColumn(part, 2); Grid.SetRow(part, 0); Grid.SetRowSpan(part, 2); }
            else if (available && i == 2) { Grid.SetColumn(part, 0); Grid.SetRow(part, 1); Grid.SetColumnSpan(part, 2); }
            else { Grid.SetColumn(part, i); Grid.SetRow(part, 0); }
        }
    }

    Control Condition(JsonObject condition, int index)
    {
        var conditions = draft.Arr("conditions");
        var row = new Grid { ColumnSpacing = 8, RowSpacing = 8, RowDefinitions = new RowDefinitions("Auto,Auto") };
        conditionRows.Add(row);
        var remove = Key(PlainButton("Remove", () =>
        {
            conditions.RemoveAt(index);
            edited = true;
            Draw();
            RequestFocus(conditions.Count > 0 ? $"condition-{Math.Min(index, conditions.Count - 1)}-property" : "condition-add");
        }), $"condition-{index}-remove");
        remove.Padding = new Thickness(8, 5);
        remove.VerticalAlignment = VerticalAlignment.Center;
        var specOptions = specs.Objects().Select(s => (s.Str("key") ?? "", s.Str("label") ?? "")).ToList();
        void Replace(JsonObject next, string control) => Act(() =>
        {
            conditions[index] = next;
            edited = true;
            Draw();
            RequestFocus($"condition-{index}-{control}");
        });
        if (!SecondaryModel.ConditionAvailable(condition, specs))
        {
            var note = HelpText("This condition is unavailable. Choose a replacement or remove it.");
            note.VerticalAlignment = VerticalAlignment.Center;
            var choices = new List<(string, string)> { ("", "Choose a condition") };
            choices.AddRange(specOptions);
            var replacement = Key(U.Select(choices, "", key =>
            {
                if (specs.Objects().FirstOrDefault(s => s.Str("key") == key) is { } spec) Replace(Initial(spec), "property");
            }), $"condition-{index}-property");
            AutomationProperties.SetName(replacement, "Replace unavailable condition");
            remove.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Children.Add(note); row.Children.Add(replacement); row.Children.Add(remove);
            LayoutCondition(row, 0);
            return row;
        }
        var field = specs.Objects().First(s => s.Str("key") == condition.Str("field"));
        var property = Key(U.Select(specOptions, condition.Str("field"), key =>
        {
            if (key == condition.Str("field")) return;
            if (specs.Objects().FirstOrDefault(s => s.Str("key") == key) is { } spec) Replace(Initial(spec), "property");
        }), $"condition-{index}-property");
        AutomationProperties.SetName(property, "Condition property");
        var op = Key(U.Select(Values(field["operators"]).Select(v => (v, OperatorNames.GetValueOrDefault(v) ?? v)).ToList(), condition.Str("op"), key =>
        {
            if (condition.Str("op") == key) return;
            var value = condition["value"].Copy();
            if (key == "between" && value is not JsonArray) value = new JsonArray(value, (Js.ToNumber(value) is var n && double.IsFinite(n) ? n : 0) + 100);
            else if (key is "in" or "not_in" && value is not JsonArray) value = new JsonArray(value);
            else if (key is not ("between" or "in" or "not_in") && value is JsonArray array) value = array.FirstOrDefault().Copy();
            Replace(new JsonObject { ["field"] = condition["field"].Copy(), ["op"] = key, ["value"] = value }, "operator");
        }), $"condition-{index}-operator");
        AutomationProperties.SetName(op, "Condition operator");
        row.Children.Add(property); row.Children.Add(op); row.Children.Add(ConditionValue(condition, field)); row.Children.Add(remove);
        AutomationProperties.SetName(remove, "Remove condition");
        LayoutCondition(row, 0);
        return row;
    }

    static string DisplayChoice(string value) =>
        value.StartsWith("poe", StringComparison.Ordinal) ? "PoE" + value[3..] : Title(value.Replace('_', ' '));

    /// <summary>div.condition-value for the field's type.</summary>
    Control ConditionValue(JsonObject c, JsonObject spec)
    {
        string? type = spec.Str("type"), op = c.Str("op");
        void Set(JsonNode? value)
        {
            if (JsonNode.DeepEquals(c["value"], value)) return;
            c["value"] = value;
            Changed();
        }
        Control Named(Control control, string label)
        {
            AutomationProperties.SetName(control, label);
            control.VerticalAlignment = VerticalAlignment.Center;
            return control;
        }
        var choices = Values(spec["choices"]).Select(v => (v, DisplayChoice(v))).ToList();
        if (type == "bool")
            return Named(U.Select([("true", "Yes"), ("false", "No")], c.Bool("value") == false ? "false" : "true", v => Set(v == "true")), "Condition value");
        if (type == "weekday" || (type == "choice" && op is "in" or "not_in"))
        {
            var list = new WrapPanel { ItemSpacing = 9, LineSpacing = 5, VerticalAlignment = VerticalAlignment.Center };
            var selectedValues = c["value"] is JsonArray ? Values(c["value"]).ToHashSet() : [];
            foreach (var (value, label) in choices)
            {
                var box = U.Check(label, selectedValues.Contains(value), on =>
                {
                    if (on) selectedValues.Add(value); else selectedValues.Remove(value);
                    Set(Strings(choices.Select(p => p.v).Where(selectedValues.Contains)));
                });
                box.FontSize = 13;
                list.Children.Add(box);
            }
            return list;
        }
        if (type == "choice") return Named(U.Select(choices, c.Str("value"), v => Set(v)), "Condition value");
        if (type == "time" || op == "between")
        {
            var pair = c.Arr("value");
            var values = new[] { Js.ToStr(pair.ElementAtOrDefault(0)), Js.ToStr(pair.ElementAtOrDefault(1)) };
            for (int i = 0; i < 2; i++) if (pair.ElementAtOrDefault(i) is null) values[i] = "";
            // Number inputs share the column; time inputs keep their own width, as the browser's time fields do.
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(type == "time" ? "Auto,Auto,Auto" : "*,Auto,*"), ColumnSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                var box = U.Input(values[i], type == "time" ? "HH:mm" : null, v =>
                {
                    if (v == values[index]) return;
                    values[index] = v;
                    Set(type == "time" ? Strings(values) : new JsonArray(values.Select(Number).ToArray()));
                });
                if (type == "time") box.Width = 110;
                AutomationProperties.SetName(box, i == 0 ? "Start" : "End");
                Grid.SetColumn(box, i * 2);
                grid.Children.Add(box);
            }
            var to = U.Text("to", "muted");
            to.FontSize = 13;
            to.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(to, 1);
            grid.Children.Add(to);
            return grid;
        }
        var unit = spec.Str("unit") is { Length: > 0 } text ? text : null;
        var threshold = U.Input(c["value"] is null ? "" : Js.ToStr(c["value"]), changed: v => Set(Number(v)));
        AutomationProperties.SetName(threshold, unit is null ? "Value" : $"Value in {unit}");
        if (unit is null) return Named(threshold, "Value");
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 7, VerticalAlignment = VerticalAlignment.Center };
        var suffix = U.Text(unit, "muted");
        suffix.FontSize = 13;
        suffix.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(suffix, 1);
        row.Children.Add(threshold); row.Children.Add(suffix);
        return row;
    }

    static JsonNode? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? JsonValue.Create(number) : null;

    /// <summary>A check box whose click checks it from the mixed state, as an HTML indeterminate box does.</summary>
    sealed class AllPortsBox : CheckBox
    {
        protected override Type StyleKeyOverride => typeof(CheckBox);
        protected override void Toggle() => SetCurrentValue(IsCheckedProperty, IsChecked != true);
    }

    RuleSection Scope()
    {
        var part = Section("Ports", null, false, out var view);
        var layout = Ports;
        var all = layout.Copper.Concat(layout.Sfp).ToList();
        var chosen = (draft["ports"] is null ? layout.Copper : PortTopology.PortIds(draft["ports"])).ToHashSet();
        var boxes = new List<(int Id, CheckBox Box)>();
        var allPorts = new AllPortsBox { Content = "All ports" };
        AutomationProperties.SetName(allPorts, "All ports");
        var count = U.Text("", "muted");
        count.FontSize = 13;
        count.VerticalAlignment = VerticalAlignment.Center;
        bool updating = false;
        void UpdateScope()
        {
            int n = boxes.Count(b => b.Box.IsChecked == true);
            updating = true;
            allPorts.IsChecked = n == boxes.Count ? true : n > 0 ? null : false;
            updating = false;
            count.Text = $"{n} of {boxes.Count} selected";
        }
        void Commit()
        {
            draft["ports"] = Js.NumberArray(boxes.Where(b => b.Box.IsChecked == true).Select(b => b.Id));
            Changed();
        }
        void Choose(IEnumerable<int> ports)
        {
            var set = ports.ToHashSet();
            updating = true;
            foreach (var (id, box) in boxes) box.IsChecked = set.Contains(id);
            updating = false;
            Commit();
            UpdateScope();
        }
        allPorts.IsCheckedChanged += (_, _) => { if (!updating) Choose(allPorts.IsChecked == true ? all : []); };
        var toolbar = new FlexRow { Gap = 6, LineGap = 6 };
        toolbar.Children.Add(allPorts);
        toolbar.Children.Add(U.Button("Ethernet", "quiet chip", () => Choose(layout.Copper)));
        toolbar.Children.Add(U.Button("SFP", "quiet chip", () => Choose(layout.Sfp)));
        toolbar.Children.Add(U.Button("None", "quiet chip", () => Choose([])));
        toolbar.Children.Add(FlexRow.Push(count));
        view.Children.Add(toolbar);
        var checks = new WrapPanel { ItemSpacing = 14, LineSpacing = 6, Margin = new Thickness(0, 8) };
        foreach (var id in all)
        {
            var box = U.Check((layout.Sfp.Contains(id) ? "SFP " : "Port ") + id, chosen.Contains(id), _ =>
            {
                if (updating) return;
                Commit();
                UpdateScope();
            });
            box.MinWidth = 45;
            boxes.Add((id, box));
            checks.Children.Add(box);
        }
        view.Children.Add(checks);
        UpdateScope();
        return part;
    }

    RuleSection Appearance(JsonObject look)
    {
        var part = Section("Appearance", null, false, out var view);
        var style = look.Str("style") ?? "solid";
        // .form-grid.look-fields: three columns; hidden fields leave the grid, as display: none does.
        var fields = new Grid { ColumnSpacing = 18 };
        for (int i = 0; i < 3; i++) fields.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 100 });
        var styleSelect = U.Select(RuleStyles, style);
        AutomationProperties.SetName(styleSelect, "Appearance");
        var styleField = U.Field("Appearance", styleSelect);

        // The saved color when it is #rrggbb, otherwise white (the hidden ruleColor input).
        var current = IsHex(look.Str("color")) ? look.Str("color")! : "#ffffff";
        look["color"] = current;
        colorText = current;
        var hex = U.Input(current);
        hex.MaxLength = 7;
        AutomationProperties.SetName(hex, "Hex color");
        // .swatch-button: 44px, the color with a 4px inset ring
        var sample = new Border { CornerRadius = new CornerRadius(7), Background = U.Brush(current) };
        var ring = new Border { CornerRadius = new CornerRadius(7), BorderThickness = new Thickness(4) }.Bind(Border.BorderBrushProperty, "swatch-inset");
        var swatch = new Button
        {
            Width = 44, Height = 44, MinHeight = 44, Padding = new Thickness(0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Panel { Children = { sample, ring } },
        };
        swatch.Classes.Add("swatch-button");
        AutomationProperties.SetName(swatch, "Rule color");
        void ShowColor(string value)
        {
            look["color"] = value;
            sample.Background = U.Brush(value);
        }
        swatch.Click += (_, _) => ColorLibraryDialog.Open(Shell, "Rule color", look.Str("color") ?? current, value => hex.Text = value);
        hex.PropertyChanged += (_, change) =>
        {
            if (change.Property != TextBox.TextProperty) return;
            var value = hex.Text ?? "";
            if (value == colorText) return;
            colorText = value;
            if (IsHex(value)) ShowColor(value);
            Changed();
        };
        var colorRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10 };
        Grid.SetColumn(hex, 1);
        hex.VerticalAlignment = VerticalAlignment.Center;
        colorRow.Children.Add(swatch); colorRow.Children.Add(hex);
        var colorField = U.Field("Color", colorRow);

        var originalRate = look.Num("rate") ?? .25;
        look["rate"] = originalRate;
        cycleInitial = Js.NumberToString(1 / originalRate);
        cycleText = cycleInitial;
        var cycle = U.Input(cycleInitial, changed: value =>
        {
            cycleText = value;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) && seconds > 0)
                look["rate"] = SecondaryModel.RuleRateFromCycle(originalRate, JsonValue.Create(cycleInitial), JsonValue.Create(value));
            Changed();
        });
        AutomationProperties.SetName(cycle, "Cycle time · seconds");
        var cycleField = U.Field("Cycle time · seconds", cycle);

        fields.Children.Add(styleField); fields.Children.Add(colorField); fields.Children.Add(cycleField);
        void UpdateStyle()
        {
            var value = look.Str("style") ?? "solid";
            colorField.IsVisible = value != "effect";
            cycleField.IsVisible = value is "pulse" or "blink" or "flash";
            int column = 1;
            if (colorField.IsVisible) Grid.SetColumn(colorField, column++);
            if (cycleField.IsVisible) Grid.SetColumn(cycleField, column);
        }
        styleSelect.SelectionChanged += (_, _) =>
        {
            if (styleSelect.Value() is not { } value || value == look.Str("style")) return;
            look["style"] = value;
            UpdateStyle();
            Changed();
        };
        UpdateStyle();
        view.Children.Add(fields);

        bool custom = look.Num("brightness") is not null;
        var level = U.Range(0, 100, 1, look.Num("brightness") ?? Workspace.State.Num("brightness") ?? 100);
        AutomationProperties.SetName(level, "Rule brightness");
        var output = U.Text($"{Js.NumberToString(Math.Round(level.Value))}%");
        output.FontSize = 13;
        output.TextAlignment = TextAlignment.Right;
        output.VerticalAlignment = VerticalAlignment.Center;
        level.IsEnabled = custom;
        level.ValueChanged += (_, _) =>
        {
            output.Text = $"{Js.NumberToString(Math.Round(level.Value))}%";
            if (!level.IsEnabled) return;
            look["brightness"] = (int)Math.Round(level.Value);
            Changed();
        };
        var check = U.Check("Set brightness", custom, on =>
        {
            level.IsEnabled = on;
            look["brightness"] = on ? (int)Math.Round(level.Value) : null;
            Changed();
        });
        var brightness = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,35"), ColumnSpacing = 10 };
        Grid.SetColumn(level, 1); Grid.SetColumn(output, 2);
        brightness.Children.Add(check); brightness.Children.Add(level); brightness.Children.Add(output);
        view.Children.Add(new EditorColumn { Limit = 580, Child = brightness });
        var help = HelpText("Set brightness replaces the default brightness for matching ports. Use port effect keeps their effect and changes only the brightness.");
        help.Margin = new Thickness(0, 5, 0, 0);
        help.MaxWidth = 470;
        help.HorizontalAlignment = HorizontalAlignment.Left;
        view.Children.Add(help);
        return part;
    }

    /// <summary>
    /// The form's own validity checks (the browser's required fields, the hex pattern and the cycle range), then
    /// validateRuleDraft. Returns the message and the section holding the field.
    /// </summary>
    (string Message, string? Section) RuleError()
    {
        var look = draft.Obj("look");
        var style = look.Str("style");
        if (style != "effect" && !IsHex(colorText))
            return ("Enter a valid color.", "Appearance");
        if (style is "pulse" or "blink" or "flash")
        {
            double initial = double.TryParse(cycleInitial, NumberStyles.Float, CultureInfo.InvariantCulture, out var start) ? start : 4;
            double min = Math.Min(CycleMinimums[style], initial), max = Math.Max(20, initial);
            if (!double.TryParse(cycleText, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || !double.IsFinite(seconds) || seconds < min || seconds > max)
                return ($"Enter a cycle time from {Js.NumberToString(min)} to {Js.NumberToString(max)} seconds.", "Appearance");
        }
        foreach (var c in draft.Objects("conditions"))
        {
            if (specs.Objects().FirstOrDefault(s => s.Str("key") == c.Str("field")) is not { } spec) continue;
            if (spec.Str("type") == "number" && (c.Str("op") == "between" ? c.Arr("value").Count != 2 || c.Arr("value").Any(n => n.Num() == null) : c.Num("value") == null))
                return ("Enter a number for each threshold.", "When");
            if (spec.Str("type") == "time" && (c.Arr("value").Count != 2 || Values(c["value"]).Any(s => !TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))))
                return ("Enter a start and end time.", "When");
        }
        var reason = SecondaryModel.ValidateRuleDraft(draft, specs);
        return (reason, reason == "Choose at least one port." ? "Ports" : reason.Length > 0 && reason != "Enter a rule name." ? "When" : null);
    }
}
