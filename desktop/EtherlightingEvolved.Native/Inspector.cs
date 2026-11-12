using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace EtherlightingEvolved.Native;

/// <summary>
/// Lighting controls for the selected ports: group effect and its settings, fixed colors, SFP indicators, and the
/// switch-wide brightness, profiles and pause. Edits stay local until an Apply button sends them.
/// </summary>
public sealed class Inspector : UserControl
{
    static readonly string[] CategoryOrder = ["static", "animated", "data", "time"];
    static readonly (string Value, string Label)[] SfpModes = [("unmanaged", "Link status"), ("off", "Off"), ("color", "Custom color")];
    static readonly (string Value, string Label)[] SfpEffects = [("steady", "Steady"), ("pulse", "Pulse"), ("color_cycle", "Color cycle"), ("two_color", "Two-color fade")];

    readonly StackPanel body = new() { Spacing = 14 };
    readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(2, 0) };
    readonly Slider master = new() { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true };
    readonly TextBlock masterValue = new() { Width = 44, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    readonly ComboBox profiles = new() { MinWidth = 180, PlaceholderText = "No saved profiles" };
    readonly Button applyProfile = new() { Content = "Apply profile" };
    readonly Button pause = new() { Content = "Pause EE lighting" };
    readonly List<Button> actions = [];
    WorkspaceApi? api;
    WorkspaceSnapshot? snapshot;
    IReadOnlyList<int> selection = [];
    string signature = "";
    bool dirty, busy, settingMaster;
    DispatcherTimer? masterTimer;

    public Inspector()
    {
        Width = 360;
        master.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            masterValue.Text = $"{master.Value:0}%";
            if (settingMaster || api is null) return;
            masterTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            masterTimer.Stop();
            masterTimer.Tick -= SendMaster;
            masterTimer.Tick += SendMaster;
            masterTimer.Start();
        };
        applyProfile.Click += async (_, _) =>
        {
            if (profiles.SelectedItem is string name) await Run(() => api!.ApplyProfileAsync(name), $"Applied profile {name}.");
        };
        pause.Click += async (_, _) =>
        {
            var release = snapshot?.Lighting?.Released != true;
            await Run(() => api!.SetReleasedAsync(release), release ? "EE lighting paused; the switch shows its own lighting." : "EE lighting resumed.");
        };
        actions.AddRange([applyProfile, pause]);
        var masterRow = new DockPanel { Children = { masterValue, master } };
        DockPanel.SetDock(masterValue, Dock.Right);
        var profileRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { profiles, applyProfile } };
        var global = Section("Switch", Labeled("Master brightness", masterRow), Labeled("Profile", profileRow), pause);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 14, Margin = new Thickness(0, 0, 12, 0), Children = { body, global, message } },
        };
    }

    public void Bind(WorkspaceApi? value)
    {
        api = value;
        signature = "";
        _ = LoadProfilesAsync();
    }

    public void Update(WorkspaceSnapshot value, IReadOnlyList<int> ports)
    {
        snapshot = value;
        var selectionChanged = !ports.SequenceEqual(selection);
        selection = ports;
        if (selectionChanged) dirty = false;
        var lighting = value.Lighting ?? LightingState.Empty;
        if (masterTimer?.IsEnabled != true)
        {
            settingMaster = true;
            master.Value = lighting.Brightness;
            masterValue.Text = $"{lighting.Brightness:0}%";
            settingMaster = false;
        }
        pause.Content = lighting.Released ? "Resume EE lighting" : "Pause EE lighting";
        foreach (var button in actions) button.IsEnabled = !busy && value.Live && api is not null;
        master.IsEnabled = value.Live && api is not null;
        var next = Signature(value, ports);
        if (next == signature || (dirty && !selectionChanged) || busy) return;
        signature = next;
        Rebuild();
    }

    static string Signature(WorkspaceSnapshot value, IReadOnlyList<int> ports)
    {
        var lighting = value.Lighting ?? LightingState.Empty;
        var parts = new List<string> { value.Live.ToString(), (value.Effects?.Count ?? 0).ToString(), string.Join(",", ports) };
        foreach (var port in ports)
        {
            if (lighting.ZoneOf(port) is { } zone) parts.Add($"{zone.Id}:{zone.EffectId}:{zone.Params.ToJsonString()}:{string.Join(",", zone.Ports)}");
            if (lighting.Overrides.TryGetValue(port, out var color)) parts.Add($"o{port}:{color}");
            if (lighting.Sfp.TryGetValue(port, out var light)) parts.Add(light.ToString());
        }
        return string.Join("|", parts);
    }

    void Rebuild()
    {
        body.Children.Clear();
        actions.RemoveAll(button => button != applyProfile && button != pause);
        if (snapshot is not { Live: true, Lighting: { } lighting } current || api is null)
        {
            body.Children.Add(Note("Lighting controls are available while EE is connected to the switch."));
            return;
        }
        var effects = current.Effects ?? [];
        var sfpPorts = selection.Where(lighting.Sfp.ContainsKey).ToList();
        var copper = selection.Where(port => !lighting.Sfp.ContainsKey(port)).ToList();
        if (selection.Count == 0)
        {
            body.Children.Add(Heading("Select ports"));
            body.Children.Add(Note("Click a port to change its lighting. Ctrl-click adds or removes ports, and Shift-click selects a range."));
            return;
        }
        var names = new List<string>();
        if (copper.Count > 0) names.Add(copper.Count == 1 ? $"Port {copper[0]}" : $"Ports {LightingModel.Ranges(copper)}");
        if (sfpPorts.Count > 0) names.Add($"SFP {LightingModel.Ranges(sfpPorts)}");
        body.Children.Add(Heading(string.Join(" · ", names)));
        if (effects.Count > 0) body.Children.Add(EffectSection(lighting, effects));
        if (copper.Count > 0) body.Children.Add(ColorSection(lighting, copper));
        if (sfpPorts.Count > 0) body.Children.Add(SfpSection(lighting, sfpPorts));
    }

    Control EffectSection(LightingState lighting, IReadOnlyList<EffectInfo> effects)
    {
        var ordered = effects.OrderBy(effect => Array.IndexOf(CategoryOrder, effect.Category) is var index and >= 0 ? index : 9).ToList();
        var exact = lighting.Zones.FirstOrDefault(zone => zone.Ports.Order().SequenceEqual(selection.Order()));
        var reference = exact ?? lighting.ZoneOf(selection[0]);
        var chosen = ordered.FirstOrDefault(effect => effect.Id == reference?.EffectId) ?? ordered[0];
        var values = Values(chosen, reference?.EffectId == chosen.Id ? reference.Params : null);
        var editors = new StackPanel { Spacing = 10 };
        var picker = new ComboBox { ItemsSource = ordered.Select(effect => effect.Name).ToList(), SelectedIndex = ordered.IndexOf(chosen), HorizontalAlignment = HorizontalAlignment.Stretch };
        var description = Note(chosen.Description);
        void Fill()
        {
            editors.Children.Clear();
            foreach (var param in chosen.Params) editors.Children.Add(Editor(param, values));
        }
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedIndex < 0) return;
            chosen = ordered[picker.SelectedIndex];
            values = Values(chosen, reference?.EffectId == chosen.Id ? reference.Params : null);
            description.Text = chosen.Description;
            dirty = true;
            Fill();
        };
        Fill();
        var apply = Action("Apply effect", true, () => api!.ApplyEffectAsync(lighting, selection, chosen.Id, values), () => $"{chosen.Name} applied.");
        var where = exact is not null ? $"Group: {exact.Name}" : "These ports become their own group. Fixed colors on them are cleared.";
        return Section("Effect", picker, description, editors, Note(where), apply);
    }

    Control ColorSection(LightingState lighting, IReadOnlyList<int> copper)
    {
        var first = copper[0];
        var manual = lighting.Overrides.GetValueOrDefault(first);
        var shown = snapshot?.Ports.FirstOrDefault(port => port.Port == first);
        var color = manual?.Color ?? (shown is null ? "#ffffff" : $"#{shown.R:x2}{shown.G:x2}{shown.B:x2}");
        var brightness = manual?.Brightness ?? 100;
        var picker = new ColorPicker { Color = Color.TryParse(color, out var start) ? start : Colors.White, HorizontalAlignment = HorizontalAlignment.Left };
        picker.ColorChanged += (_, e) => { color = Hex(e.NewColor); dirty = true; };
        var level = NumberEditor("Brightness", "%", 0, 100, 1, brightness, value => brightness = (int)value);
        var apply = Action("Apply color", true, () => api!.ApplyColorAsync(copper, color, brightness), () => "Fixed color applied.");
        var follow = Action("Follow effect", false, () => api!.FollowEffectAsync(copper.Where(lighting.Overrides.ContainsKey)), () => "These ports follow their group effect again.");
        follow.IsEnabled &= copper.Any(lighting.Overrides.ContainsKey);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { apply, follow } };
        return Section("Fixed color", Labeled("Color", picker), level, row);
    }

    Control SfpSection(LightingState lighting, IReadOnlyList<int> ports)
    {
        var light = lighting.Sfp[ports[0]];
        var grouped = ports.Any(port => lighting.Sfp[port].EffectZone is not null);
        var settings = new JsonObject
        {
            ["mode"] = light.Mode, ["effect"] = light.Effect, ["color"] = light.Color, ["secondary_color"] = light.SecondaryColor,
            ["brightness"] = light.Brightness, ["period"] = light.Period,
        };
        var children = new List<Control>();
        if (grouped)
            children.Add(Note("These indicators run their group's effect, so only brightness changes here. Apply an effect to other ports to release them."));
        else
        {
            children.Add(Labeled("Mode", Choice(SfpModes, light.Mode, value => settings["mode"] = value)));
            children.Add(Labeled("Effect", Choice(SfpEffects, light.Effect, value => settings["effect"] = value)));
            var primary = new ColorPicker { Color = Color.TryParse(light.Color, out var first) ? first : Colors.White, HorizontalAlignment = HorizontalAlignment.Left };
            primary.ColorChanged += (_, e) => { settings["color"] = Hex(e.NewColor); dirty = true; };
            var secondary = new ColorPicker { Color = Color.TryParse(light.SecondaryColor, out var second) ? second : Colors.White, HorizontalAlignment = HorizontalAlignment.Left };
            secondary.ColorChanged += (_, e) => { settings["secondary_color"] = Hex(e.NewColor); dirty = true; };
            children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { Labeled("Color", primary), Labeled("Second color", secondary) } });
            children.Add(NumberEditor("Cycle", " s", 2, 30, 1, light.Period, value => settings["period"] = value));
        }
        children.Add(NumberEditor("Brightness", "%", 0, 100, 1, light.Brightness, value => settings["brightness"] = (int)value));
        children.Add(Action("Apply SFP lighting", true,
            () => api!.ApplySfpAsync(ports, grouped ? new JsonObject { ["brightness"] = settings["brightness"]!.DeepClone() } : settings),
            () => "SFP lighting applied."));
        return Section("SFP indicator", [.. children]);
    }

    Control Editor(ParamInfo param, JsonObject values)
    {
        var current = values[param.Key];
        switch (param.Type)
        {
            case "color":
                var picker = new ColorPicker { Color = Color.TryParse(Text(current) ?? "", out var parsed) ? parsed : Colors.White, HorizontalAlignment = HorizontalAlignment.Left };
                picker.ColorChanged += (_, e) => { values[param.Key] = Hex(e.NewColor); dirty = true; };
                return Labeled(param.Label, picker);
            case "bool":
                var box = new CheckBox { Content = param.Label, IsChecked = current is JsonValue flag && flag.TryGetValue<bool>(out var on) && on };
                box.PropertyChanged += (_, e) =>
                {
                    if (e.Property != ToggleButton.IsCheckedProperty) return;
                    values[param.Key] = box.IsChecked == true;
                    dirty = true;
                };
                return box;
            case "choice":
                return Labeled(param.Label, Choice(param.Choices.Select(choice => (choice, Title(choice))).ToArray(), Text(current) ?? param.Choices.FirstOrDefault() ?? "",
                    value => values[param.Key] = value));
            default:
                var number = current is JsonValue value && value.TryGetValue<double>(out var parsedNumber) ? parsedNumber : param.Min;
                return NumberEditor(param.Label, param.Unit is { Length: > 0 } unit ? " " + unit : "", param.Min, param.Max, param.Step, number,
                    result => values[param.Key] = param.Type == "int" ? JsonValue.Create((int)Math.Round(result)) : JsonValue.Create(result));
        }
    }

    Control NumberEditor(string label, string unit, double min, double max, double step, double value, Action<double> changed)
    {
        step = step > 0 ? step : 1;
        var slider = new Slider { Minimum = min, Maximum = Math.Max(max, min), Value = Math.Clamp(value, min, Math.Max(max, min)), SmallChange = step, TickFrequency = step, IsSnapToTickEnabled = true };
        string Format(double number) => (step >= 1 ? number.ToString("0", CultureInfo.InvariantCulture) : number.ToString("0.##", CultureInfo.InvariantCulture)) + unit;
        var shown = new TextBlock { Text = Format(slider.Value), Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            var snapped = Math.Round(Math.Round((slider.Value - min) / step) * step + min, 4);
            shown.Text = Format(snapped);
            changed(snapped);
            dirty = true;
        };
        var row = new DockPanel { Children = { shown, slider } };
        DockPanel.SetDock(shown, Dock.Right);
        return Labeled(label, row);
    }

    ComboBox Choice((string Value, string Label)[] options, string selected, Action<string> changed)
    {
        var box = new ComboBox
        {
            ItemsSource = options.Select(option => option.Label).ToList(), MinWidth = 180,
            SelectedIndex = Math.Max(0, Array.FindIndex(options, option => option.Value == selected)),
        };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            changed(options[box.SelectedIndex].Value);
            dirty = true;
        };
        return box;
    }

    Button Action(string label, bool primary, Func<Task<string?>> send, Func<string> done)
    {
        var button = new Button { Content = label, IsEnabled = !busy };
        if (primary) button.Classes.Add("accent");
        button.Click += async (_, _) => await Run(send, done());
        actions.Add(button);
        return button;
    }

    async Task Run(Func<Task<string?>> send, string done)
    {
        if (busy || api is null) return;
        busy = true;
        foreach (var button in actions) button.IsEnabled = false;
        Say("Applying…", Palette.Muted);
        var error = await send();
        busy = false;
        dirty = false;
        signature = "";
        foreach (var button in actions) button.IsEnabled = snapshot?.Live == true;
        Say(error ?? done, error is null ? Palette.Live : Palette.Error);
        if (snapshot is not null) Update(snapshot, selection);
        if (error is null) _ = LoadProfilesAsync();
    }

    async void SendMaster(object? sender, EventArgs e)
    {
        masterTimer?.Stop();
        if (api is null) return;
        var error = await api.SetBrightnessAsync(master.Value);
        if (error is not null) Say(error, Palette.Error);
    }

    async Task LoadProfilesAsync()
    {
        if (api is null) return;
        var names = await api.ProfilesAsync();
        var keep = profiles.SelectedItem as string;
        profiles.ItemsSource = names;
        profiles.SelectedItem = keep is not null && names.Contains(keep) ? keep : names.FirstOrDefault();
    }

    void Say(string text, IBrush tone)
    {
        message.Text = text;
        message.Foreground = tone;
    }

    static JsonObject Values(EffectInfo effect, JsonObject? saved)
    {
        var values = new JsonObject();
        foreach (var param in effect.Params)
            values[param.Key] = saved?[param.Key]?.DeepClone() ?? param.Default?.DeepClone();
        return values;
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    static string Title(string value) => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..].Replace('_', ' ');

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };

    static TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap };

    static Control Labeled(string label, Control control) => new StackPanel
    {
        Spacing = 4, Children = { new TextBlock { Text = label, FontSize = 12, Foreground = Palette.Muted }, control },
    };

    static Control Section(string title, params Control[] children)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = Palette.Muted });
        foreach (var child in children) panel.Children.Add(child);
        return new Border { Background = Palette.Panel, CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = panel };
    }
}
