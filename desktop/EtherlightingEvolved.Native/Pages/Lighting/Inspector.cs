using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Ui;
using U = EtherlightingEvolved.Native.Ui.Ui;
using LS = EtherlightingEvolved.Native.Core.Model.LightingState;

namespace EtherlightingEvolved.Native.Pages.Lighting;

public sealed partial class LightingPage
{
    readonly HashSet<TextBox> invalidFields = [];
    internal bool ValidateEditor()
    {
        if (invalidFields.FirstOrDefault() is not { } field) return true;
        field.Focus(); return false;
    }
    bool moreOpen;
    JsonObject? CurrentEffect()
    {
        var effects = editor.Selected.Select(id => EffectDraft.PortEffect(State, editor.Draft, id)).ToArray();
        return effects.Length > 0 && effects.All(e => e.Str("id") == effects[0].Str("id") && JsonNode.DeepEquals(e.At("params"), effects[0].At("params"))) ? effects[0] : null;
    }
    void BuildInspector()
    {
        invalidFields.Clear();
        inspector.Children.Clear(); var heading = U.H2("Port controls");
        var header = new Grid { ColumnDefinitions = new("*,Auto") };
        var collapse = U.IconButton("panel", "Hide port controls", () => { if (Bounds.Width >= 701) SetInspectorCollapsed(true); mobileInspector = false; Reflow(true); });
        header.Children.Add(heading); header.Children.Add(collapse); Grid.SetColumn(collapse, 1); inspector.Children.Add(header);
        if (editor.Selected.Count == 0) { inspector.Children.Add(U.P("Select a port on the map, or choose All ports.", "muted")); return; }
        inspector.Children.Add(U.P(editor.Scope(), "muted"));
        var sources = editor.Selected.Select(p => LS.ControlSource(State, p, Workspace.Effects)).ToList();
        var labels = sources.Select(s => s.Str("label")).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (labels.Count > 0) inspector.Children.Add(U.Text(labels.Count == 1 ? "•  Applied: " + labels[0] : "Multiple control sources", "small muted"));
        if (sources.FirstOrDefault(s => s.Str("kind") == "rule") is { } rule)
            inspector.Children.Add(Action("Edit automation", async () => { editor.RequestedRuleId = rule.Str("rule.id"); await Shell.Navigate("Automation"); }));
        bool optical = editor.SelectedCopper.Count == 0, independent = optical && editor.GroupedOptical.Count == 0;
        if (independent) OpticalInspector();
        else
        {
            if (optical) editor.Editor = "effect";
            if (!optical)
            {
                var fixedMode = U.Button("Fixed color", editor.Editor == "color" ? "primary" : "quiet", () => { editor.Editor = "color"; editor.Signal(); });
                var effects = U.Button("Effect", editor.Editor == "effect" ? "primary" : "quiet", () => { editor.Editor = "effect"; editor.Signal(); });
                var modes = U.Wrap(4, fixedMode, effects);
                if (editor.Editor == "effect") modes.Children.Add(EffectChooser());
                inspector.Children.Add(modes);
            }
            if (optical) inspector.Children.Add(EffectChooser());
            if (editor.Editor == "color") FixedInspector(); else EffectInspector();
            if (editor.GroupedOptical.Count > 0)
                inspector.Children.Add(U.Button("Return to SFP lighting", "quiet", () => editor.Edit(d => EffectDraft.LeaveSfpEffect(d, editor.GroupedOptical), true)));
        }
        var copy = U.Button("Copy appearance", "quiet", CopyAppearance); copy.IsEnabled = editor.Selected.Count == 1;
        var paste = U.Button("Paste appearance", "quiet", () => { try { PasteAppearance(); } catch (Exception ex) { Shell.Notify(ex.Message, true); } }); paste.IsEnabled = editor.Appearance.HasValue;
        inspector.Children.Add(U.Wrap(5, copy, paste));
    }
    void Gallery(string type)
    {
        editor.GalleryState["type"] = type; _ = Shell.Run(() => Shell.Navigate("Effects"));
    }
    static string Label(string text) => string.Join(" ", text.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    static (string, string)[] Options(params (string, string)[] options) => options;
    Button EffectChooser()
    {
        var spec = Workspace.Effects.Objects().FirstOrDefault(s => s.Str("id") == CurrentEffect().Str("id"));
        var caption = spec.Str("name") ?? (editor.Selected.Count > 1 ? "Multiple effects" : "Choose effect");
        var button = U.Button(caption + "  ›", "", () => Gallery("ethernet"));
        AutomationProperties.SetName(button, "Choose effect: " + caption); return button;
    }
    void EffectInspector()
    {
        var current = CurrentEffect(); var id = current.Str("id"); var spec = Workspace.Effects.Objects().FirstOrDefault(s => s.Str("id") == id);
        if (current is null || spec is null || id is null) return;
        var values = Js.Spread(EffectRenderer.DefaultConfig(id)["params"], current["params"]);
        StackPanel color = U.Column(12), motion = U.Column(10), uplink = U.Column(10), more = U.Column(10);
        void Edit(string k, JsonNode v, bool rebuild = false)
        {
            var active = CurrentEffect() ?? current;
            var latest = Js.Spread(EffectRenderer.DefaultConfig(id)["params"], active["params"]);
            var cfg = new JsonObject { ["id"] = id, ["params"] = latest };
            cfg["params"] = EffectRenderer.ParamsAfterChange(cfg, k, v, State["zones"]); cfg["ports"] = Js.NumberArray(editor.Selected);
            editor.StageEffect(cfg, rebuild);
        }
        var parameters = spec.Objects("params").ToList();
        foreach (var p in parameters)
        {
            var key = p.Str("key")!; var type = p.Str("type"); var name = p.Str("label") ?? Label(key);
            if (p.ContainsKey("dims") || p.ContainsKey("randomizes")) continue;
            if (id == "traffic" && key == "uplink_color" && values.Bool("custom_uplink") != true) continue;
            if (key == "color_transition" && !parameters.Any(c => c.Str("random_param") is { } r && values.Bool(r) == true)) continue;
            bool synced = values.Str("playback") == "synchronized";
            if (id == "breathing" && (key == "pace" && !synced || (key is "period" or "floor" or "ripple") && synced)) continue;
            var val = values[key] ?? p["default"]; var target = id == "traffic" && (key is "custom_uplink" or "uplink_color") ? uplink :
                type == "color" ? color : id == "daylight" || key is "speed" or "rate" or "period" or "pace" or "playback" or "color_transition" ? motion : more;
            if (type == "color")
            {
                var bright = p.Str("brightness_param"); var random = p.Str("random_param");
                target.Children.Add(ColorEditor(name, val.Str(), bright is null ? 100 : values.Num(bright) ?? 100, v => Edit(key, v), v => { if (bright is not null) Edit(bright, v); }, true, bright is not null));
                if (!synced && random is not null) target.Children.Add(U.Check("Randomize", values.Bool(random) == true, v => Edit(random, v, true)));
            }
            else if (type == "bool") target.Children.Add(U.Check(name, val.Bool() == true, v => Edit(key, v, true)));
            else if (type == "choice") target.Children.Add(U.Field(name, U.Select(p.Arr("choices").Select(n => (n.Str()!, Label(n.Str()!))).ToList(), val.Str(), v => Edit(key, v, true))));
            else target.Children.Add(NumberRange(name, p.Num("min") ?? 0, p.Num("max") ?? 100, p.Num("step") ?? (type == "int" ? 1 : .1), val.Num() ?? 0,
                v => Edit(key, type == "int" ? JsonValue.Create((int)Math.Round(v))! : JsonValue.Create(v)!),
                v => key switch { "pace" => v switch { 1 => "Slow · 3.08 s", 2 => "Medium · 2 s", 3 => "Fast · 1 s", _ => v.ToString() }, "floor" => $"{v * 100:0}%", _ => v.ToString("0.###") + (p.Str("unit") switch { "%" => "%", "cycles/s" => " cycles / second", { } unit => " " + unit, null => "" }) }));
            if (p.Str("description") is { } desc) target.Children.Add(U.P(desc, "small muted"));
        }
        foreach (var (name, section) in new[] { ("Appearance", color), ("Uplink", uplink), ("Motion", motion) }) if (section.Children.Count > 0) { inspector.Children.Add(U.H3(name)); inspector.Children.Add(section); }
        if (more.Children.Count > 0)
        {
            var expander = new Expander { Header = "More settings", Content = more, IsExpanded = moreOpen, HorizontalAlignment = HorizontalAlignment.Stretch };
            expander.PropertyChanged += (_, e) => { if (e.Property == Expander.IsExpandedProperty) moreOpen = expander.IsExpanded; }; inspector.Children.Add(expander);
        }
        inspector.Children.Add(Action("Saved effect settings", () => Library("effect")));
    }
    Control NumberRange(string label, double min, double max, double step, double value, Action<double> changed, Func<double, string> show, bool mixed = false)
    {
        var display = U.Text(mixed ? "Mixed" : show(value), "muted"); var range = U.Range(min, max, step, value, n => { display.Text = show(n); changed(n); });
        AutomationProperties.SetName(range, label); return U.Column(4, U.Wrap(12, U.Text(label), display), range);
    }
    Control ColorEditor(string label, string? color, double level, Action<string> changeColor, Action<double> brightness, bool relative = false, bool showLevel = true, bool mixedLevel = false)
    {
        string initial = ColorMath.NormalizeHex(color) ?? "#808080";
        var sample = new Border { Width = 30, Height = 29, CornerRadius = new CornerRadius(4), Background = U.Brush(initial) };
        var swatch = U.Button(""); swatch.Content = sample; swatch.Padding = new Thickness(4); swatch.Width = 42; swatch.Height = 40; AutomationProperties.SetName(swatch, label);
        var hex = U.Input(color?.ToUpperInvariant(), "#409EFF"); hex.MaxLength = 7; AutomationProperties.SetName(hex, label + " hex color");
        var error = U.P("", "error"); error.IsVisible = false;
        bool busy = false;
        void Validate()
        {
            if (busy) return;
            var value = ColorMath.NormalizeHex(hex.Text);
            if (value is null && (color is not null || !string.IsNullOrWhiteSpace(hex.Text))) { invalidFields.Add(hex); error.Text = "Enter a six-digit hex color."; error.IsVisible = true; }
            else { invalidFields.Remove(hex); error.IsVisible = false; if (value is not null && value != initial) { initial = value; sample.Background = U.Brush(value); changeColor(value); } }
            UpdateControls();
        }
        hex.TextChanged += (_, _) => Validate();
        swatch.Click += (_, _) => ColorPicker(label, initial, value => { busy = true; initial = value; sample.Background = U.Brush(value); hex.Text = value; busy = false; invalidFields.Remove(hex); error.IsVisible = false; changeColor(value); });
        var controls = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 7 }; controls.Children.Add(swatch); controls.Children.Add(hex); Grid.SetColumn(hex, 1);
        var field = U.Column(7, U.Text(label), controls, error);
        if (showLevel) field.Children.Add(NumberRange(relative ? "Color level" : "Brightness", 0, 100, 1, level, brightness, n => $"{n:0}%", mixedLevel));
        if (relative && (editor.Draft.Num("brightness") ?? State.Num("brightness") ?? 100) is < 100 and { } masterLevel)
            field.Children.Add(U.Text($"Configured peak: {Math.Round(level * masterLevel / 100):0}%", "small muted"));
        return field;
    }
    void FixedInspector()
    {
        var settings = editor.Selected.Select(p => LS.FixedColorSettings(State, p, editor.Draft)).ToList();
        var color = settings.Select(s => s.Str("color")).Distinct().Count() == 1 ? settings[0].Str("color") : null;
        var levels = settings.Select(s => LS.FixedColorBrightness(s, State, editor.Draft)).ToList();
        inspector.Children.Add(U.H3("Appearance"));
        inspector.Children.Add(ColorEditor("Color", color, levels[0], v => StageFixed("color", v), v => StageFixed("brightness", v), false, true, levels.Distinct().Count() > 1));
        inspector.Children.Add(U.P("Apply keeps these ports at their previewed color and brightness.", "small muted"));
        var follow = U.Button("Resume group effect", "quiet", () =>
        {
            editor.Editor = "effect"; editor.Edit(d =>
            {
                d["returnToEffect"] = Js.NumberArray(PortTopology.PortIds(d["returnToEffect"]).Concat(editor.SelectedCopper).Distinct());
                foreach (var p in editor.SelectedCopper) d.Obj("colors").Remove(p.ToString()); return d;
            }, true);
        });
        follow.IsEnabled = editor.SelectedCopper.Any(id => editor.Draft.Obj("colors").ContainsKey(id.ToString()) || State.Obj("overrides").ContainsKey(id.ToString()) && !PortTopology.PortIds(editor.Draft["returnToEffect"]).Contains(id) && EffectDraft.EffectDraftForPort(editor.Draft, id).Bool("clearOverrides") != true);
        inspector.Children.Add(follow);
    }
    void StageFixed(string field, JsonNode value)
    {
        editor.Edit(d =>
        {
            var optical = editor.SelectedOptical;
            if (optical.Count > 0 && SfpSyncModel.SfpSync(State, d).Str("mode") != "ethernet")
            {
                d = EffectDraft.LeaveSfpEffect(d, editor.GroupedOptical);
                foreach (var id in SfpSyncModel.SfpEditTargets(State, optical, d))
                {
                    var light = LS.SfpSettings(SfpSyncModel.EditableSfpLight(State, id, d)); light["mode"] = "color"; light["effect"] = "steady"; light[field == "brightness" ? "color_brightness" : field] = value.Copy();
                    LightingStudio.Ensure(d, "sfp")[id.ToString()] = light;
                }
            }
            foreach (var p in editor.SelectedCopper) { var settings = LS.FixedColorSettings(State, p, d); settings[field] = value.Copy(); LightingStudio.Ensure(d, "colors")[p.ToString()] = settings; }
            return d;
        });
    }
    void OpticalInspector()
    {
        var ports = editor.SelectedOptical; var lights = ports.Select(x => SfpSyncModel.EditableSfpLight(State, x, editor.Draft)).ToList();
        var first = lights[0]; var sync = SfpSyncModel.SfpSync(State, editor.Draft); bool native = SfpSyncModel.SfpNativeAvailable(State);
        bool Same(string key) => lights.All(l => JsonNode.DeepEquals(first[key], l[key]));
        if (State["sfp_sync"] is not null)
        {
            inspector.Children.Add(U.H3("Synchronization"));
            inspector.Children.Add(U.Field("SFP synchronization", U.Select(Options(("independent", "Independent"), ("together", "Together"), ("ethernet", "Follow Ethernet port")), sync.Str("mode"), mode =>
            {
                var next = Js.Spread(sync, new JsonObject { ["mode"] = mode });
                editor.Set("sfp_sync", next, true);
                if (mode != "independent") editor.Select(editor.Optical.Where(id => EffectDraft.PortEffect(State, editor.Draft, id) is null));
            })));
            if (sync.Str("mode") == "ethernet")
            {
                inspector.Children.Add(U.Field("Source port", U.Select(State.Objects("ports").Select(p => (p.Int("port").ToString()!, $"Port {p.Int("port")} · {Core.Model.PortMap.PortLabel(p).Str("text")}")).ToList(), sync.Int("source_port").ToString(), key => { if (int.TryParse(key, out var id)) editor.Set("sfp_sync", Js.Spread(sync, new JsonObject { ["source_port"] = id })); })));
                inspector.Children.Add(U.Button("Browse port effects", "quiet", () => Gallery("ethernet"))); return;
            }
        }
        string? mode = Same("mode") ? first.Str("mode") : "mixed", effect = Same("effect") ? first.Str("effect") : "mixed";
        var modes = Options(("unmanaged", "Link status"), ("color", "Custom lighting"), ("off", "Off")).ToList();
        if (mode == "mixed") modes.Insert(0, ("mixed", "Mixed"));
        inspector.Children.Add(U.H3("Appearance")); inspector.Children.Add(U.Field("Lighting", U.Select(modes, mode, v => { if (v != "mixed") StageOptical(_ => new JsonObject { ["mode"] = v }, true); })));
        if (mode == "color")
        {
            var effects = Options(("steady", "Steady"), ("pulse", "Gentle pulse"), ("two_color", "Two-color fade"), ("color_cycle", "Color cycle")).ToList();
            if (effect == "mixed") effects.Insert(0, ("mixed", "Mixed"));
            inspector.Children.Add(U.Field("Effect", U.Select(effects, effect, v => { if (v != "mixed") StageOptical(l => SfpSyncModel.SfpEffectChange(l, v, native), true); })));
            void ColorField(string key, string dim, string name) => inspector.Children.Add(ColorEditor(name, Same(key) ? first.Str(key) : null, first.Num(dim) ?? 100, v => StageOptical(_ => new JsonObject { [key] = v }), v => StageOptical(_ => new JsonObject { [dim] = v }), true, true, !Same(dim)));
            if (effect != "color_cycle") ColorField("color", "color_brightness", "Primary color");
            if (effect == "two_color") ColorField("secondary_color", "secondary_brightness", "Secondary color");
            if (effect == "color_cycle") inspector.Children.Add(NumberRange("Color level", 0, 100, 1, first.Num("color_brightness") ?? 100, v => StageOptical(_ => new JsonObject { ["color_brightness"] = v }), v => $"{v:0}%"));
            if (effect is not ("steady" or "mixed"))
            {
                var range = SfpSyncModel.SfpDurationRange(effect, first.Num("period") ?? 6, native); inspector.Children.Add(U.H3("Motion"));
                inspector.Children.Add(NumberRange("Cycle duration", range.Num("min") ?? 1, range.Num("max") ?? 30, range.Num("step") ?? 1, range.Num("value") ?? 6, v => StageOptical(_ => new JsonObject { ["period"] = v }), n => $"{n:0.##} s", !Same("period")));
            }
        }
        else inspector.Children.Add(U.P(mode switch { "off" => "The selected lights are turned off.", "unmanaged" => "The indicator follows the port’s link status.", _ => "Choose a lighting mode for the selected SFP ports." }, "small muted"));
        inspector.Children.Add(U.Button("Browse port effects", "quiet", () => Gallery("ethernet")));
    }
    void StageOptical(Func<JsonObject, JsonObject> change, bool rebuild = false)
    {
        editor.Edit(d =>
        {
            foreach (var id in SfpSyncModel.SfpEditTargets(State, editor.SelectedOptical, d))
            {
                var light = SfpSyncModel.EditableSfpLight(State, id, d); LightingStudio.Ensure(d, "sfp")[id.ToString()] = SfpSyncModel.NormalizeSfpBrightness(Js.Spread(LS.SfpSettings(light), change(light)));
            }
            return d;
        }, rebuild);
    }
    void CopyAppearance()
    {
        if (editor.Selected.Count != 1) return;
        int id = editor.Selected.First(); var effect = EffectDraft.PortEffect(State, editor.Draft, id);
        var fixedColor = editor.Draft.At($"colors.{id}") ?? (!PortTopology.PortIds(editor.Draft["returnToEffect"]).Contains(id) && EffectDraft.EffectDraftForPort(editor.Draft, id).Bool("clearOverrides") != true ? State.At($"overrides.{id}") : null);
        if (editor.Optical.Contains(id) && effect is null) editor.Appearance = ("sfp", LS.SfpSettings(SfpSyncModel.EditableSfpLight(State, id, editor.Draft)));
        else if (fixedColor is JsonObject fc) editor.Appearance = ("color", (JsonObject)fc.DeepClone());
        else if (effect is not null) editor.Appearance = ("effect", (JsonObject)effect.DeepClone());
        else { Shell.Notify("Choose a color or effect for this port first."); return; }
        BuildInspector(); Shell.Notify("Appearance copied");
    }
    void PasteAppearance()
    {
        if (editor.Appearance is not { } copy) return;
        if (copy.Type == "effect") { var cfg = Js.Spread(copy.Settings, new JsonObject { ["ports"] = Js.NumberArray(editor.Selected), ["clearOverrides"] = true }); editor.Editor = "effect"; editor.StageEffect(cfg); return; }
        var optical = editor.SelectedOptical; bool following = SfpSyncModel.SfpSync(State, editor.Draft).Str("mode") == "ethernet";
        if (copy.Type == "sfp" && (optical.Count == 0 || following)) { Shell.Notify(optical.Count == 0 ? "Select SFP ports for these settings." : "Select Independent or Together before pasting this appearance."); return; }
        var masterLevel = editor.Draft.Num("brightness") ?? State.Num("brightness") ?? 100; var bright = copy.Settings.Num("brightness") ?? masterLevel;
        if (copy.Type == "color" && !following && optical.Count > 0 && bright > masterLevel) { Shell.Notify($"Raise Default brightness to at least {bright:0}% before pasting this appearance onto SFP lights.", true); return; }
        editor.Edit(d =>
        {
            if (optical.Count > 0 && !following)
            {
                d = EffectDraft.LeaveSfpEffect(d, editor.GroupedOptical);
                var light = copy.Type == "sfp" ? SfpSyncModel.NormalizeSfpBrightness(copy.Settings) : new JsonObject { ["mode"] = "color", ["effect"] = "steady", ["color"] = copy.Settings["color"].Copy(), ["brightness"] = 100, ["color_brightness"] = copy.Settings["brightness"] is null ? 100 : masterLevel == 0 ? 0 : Math.Round(bright * 100 / masterLevel) };
                foreach (var id in SfpSyncModel.SfpEditTargets(State, optical, d)) LightingStudio.Ensure(d, "sfp")[id.ToString()] = light.DeepClone();
            }
            if (copy.Type == "color") { editor.Editor = "color"; foreach (var id in editor.SelectedCopper) LightingStudio.Ensure(d, "colors")[id.ToString()] = copy.Settings.DeepClone(); }
            return d;
        }, true);
    }
}
