using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Profiles;

/// <summary>Port of renderProfiles (secondary.mjs:582-727).</summary>
internal sealed class ProfilesPage : ManagementPage
{
    [ModuleInitializer] internal static void Register() => PageRegistry.Register("Profiles", shell => new ProfilesPage(shell));
    JsonArray records = [];
    string? selected;
    string? fingerprint;
    JsonObject state = [];
    readonly List<Action<double>> strips = [];
    readonly Stopwatch elapsed = Stopwatch.StartNew();
    readonly DispatcherTimer animation = new() { Interval = TimeSpan.FromMilliseconds(120) };
    Button? undo;
    bool loaded, failed;

    public ProfilesPage(AppShell shell) : base(shell)
    {
        animation.Tick += (_, _) => Paint();
        state = Workspace.State;
        Draw();
        Start(Reload);
    }

    async Task Reload()
    {
        try
        {
            var profiles = Get("/api/profiles");
            var current = Get("/api/state");
            await Task.WhenAll(profiles, current);
            if (!Active) return;
            records = (await profiles).Arr("profiles");
            state = Merge(await current);
            if (!records.Objects().Any(r => r.Str("name") == selected)) selected = records.Objects().FirstOrDefault()?.Str("name");
            loaded = true;
            failed = false;
            Draw();
            Tell("");
        }
        catch
        {
            if (!loaded && Active)
            {
                failed = true;
                Draw();
            }
            throw;
        }
    }

    /// <summary>current = {...getState(), ...stateResponse}</summary>
    JsonObject Merge(JsonNode? response)
    {
        var merged = (JsonObject)Workspace.State.DeepClone();
        foreach (var entry in response.Obj()) merged[entry.Key] = entry.Value.Copy();
        return merged;
    }

    /// <summary>afterWrite: read the profiles and state again, redraw, then report the result.</summary>
    async Task AfterWrite(string message)
    {
        bool refreshed = true;
        try { records = (await Get("/api/profiles")).Arr("profiles"); } catch { refreshed = false; }
        try
        {
            await Workspace.Refresh(false);
            state = Merge(await Get("/api/state"));
        }
        catch { refreshed = false; }
        if (!Active) return;
        try { Draw(); } catch { refreshed = false; }
        Tell(refreshed ? message : $"{message} Status could not be refreshed. Use Refresh profiles to check it.", !refreshed);
    }

    /// <summary>nameDialog: the profile name form. A name equal to <paramref name="unchanged"/> closes the form without checks.</summary>
    async Task<string?> AskName(string title, string initial = "", string? unchanged = null)
    {
        var field = U.Input(initial);
        field.MaxLength = 64;
        AutomationProperties.SetName(field, "Profile name");
        var error = U.Text("", "danger", true);
        error.FontSize = 13;
        error.MinHeight = 19;
        error.Margin = new Thickness(0, 8, 0, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Border { Child = buttons, Margin = new Thickness(0, 20, 0, 0), Padding = new Thickness(0, 15, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) }
            .Bind(Border.BorderBrushProperty, "line");
        var heading = U.Text(title, "h2", true);
        heading.FontSize = 18;
        heading.Margin = new Thickness(0, 0, 0, 16);
        var body = U.Column(0, heading, U.Field("Profile name", field), error, footer);
        var dialog = Shell.Overlay.Show(body, width: 400, sheet: true);
        string? answer = null;
        string Validate(string value) =>
            value == unchanged ? "" : SecondaryModel.ProfileNameError(value) is { Length: > 0 } reason ? reason
                : records.Objects().Any(r => r.Str("name") == value) ? "A profile with this name already exists." : "";
        void ShowError(string message)
        {
            error.Text = message;
            field.Classes.Set("invalid", message.Length > 0);
        }
        void Accept()
        {
            string name = field.Text?.Trim() ?? "";
            if (name == unchanged) { answer = name; dialog.Close(); return; }
            var reason = Validate(name);
            if (reason.Length > 0) { ShowError(reason); field.Focus(); return; }
            ShowError("");
            answer = name;
            dialog.Close();
        }
        field.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.TextProperty && field.Classes.Contains("invalid")) ShowError(Validate(field.Text?.Trim() ?? ""));
        };
        buttons.Children.Add(U.Button("Cancel", null, () => dialog.Close()));
        buttons.Children.Add(U.Button(title, "primary", Accept));
        field.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; Accept(); } };
        Dispatcher.UIThread.Post(() => { field.Focus(); field.SelectAll(); }, DispatcherPriority.Loaded);
        await dialog.Result;
        return answer;
    }

    async Task Save()
    {
        bool preview = Studio.PreviewScene() != null;
        var name = await AskName(preview ? "Save preview as profile" : "Save profile");
        if (name == null) return;
        if (preview) await Studio.SavePreviewProfile(name);
        else await Send("/api/presets", "POST", new JsonObject { ["name"] = name, ["overwrite"] = false });
        selected = name;
        await AfterWrite(preview ? "Preview saved. Choose Apply profile when you want to use it." : "Profile saved.");
    }

    async Task Apply(JsonObject record)
    {
        if (!await Studio.ConfirmDraft()) return;
        await Studio.ApplyProfile(record);
        if (!Active) return;
        state = Workspace.State;
        Draw();
        Tell("Profile applied.");
    }

    async Task Undo()
    {
        if (!Studio.CanUndoApply) return;
        await Studio.UndoLastApply();
        // UndoLastApply clears the remembered apply only when it restored the earlier lighting.
        if (Studio.CanUndoApply) return;
        await AfterWrite("Previous lighting restored.");
    }

    async Task Change(JsonObject record, string action)
    {
        string name = record.Str("name")!;
        var payload = new JsonObject { ["action"] = action, ["expected_revision"] = record["revision"].Copy() };
        string message;
        string? next = selected;
        switch (action)
        {
            case "replace":
                if (!await Studio.ConfirmDraft() || !await Shell.Overlay.Confirm("Update profile?", $"Replace “{name}” with the lighting currently applied to the switch?", "Update")) return;
                message = "Profile updated.";
                break;
            case "rename":
            case "duplicate":
                string initial = action == "rename" ? name : name[..Math.Min(name.Length, 59)] + " copy";
                next = await AskName(action == "rename" ? "Rename profile" : "Duplicate profile", initial, action == "rename" ? name : null);
                if (string.IsNullOrEmpty(next)) return;
                if (action == "rename" && next == name) { Tell("Profile name unchanged."); return; }
                payload["name"] = next;
                message = action == "rename" ? "Profile renamed." : "Profile duplicated.";
                break;
            case "delete":
                var schedules = record.Objects("schedules").Where(s => s.Truthy("enabled")).Select(s => $"“{s.Str("name")}”").ToList();
                var warning = schedules.Count > 0 ? $" This also disables {string.Join(", ", schedules)}." : "";
                if (!await Shell.Overlay.Confirm("Delete profile?", $"Remove “{name}” from your saved profiles? Your current lighting keeps running.{warning}", "Delete")) return;
                payload["disable_schedules"] = schedules.Count > 0;
                next = null;
                message = "Profile deleted.";
                break;
            default: return;
        }
        records = (await Send("/api/profiles/" + Uri.EscapeDataString(record.Str("id")!), "PATCH", payload)).Arr("profiles");
        selected = next ?? records.Objects().FirstOrDefault()?.Str("name");
        await AfterWrite(message);
    }

    static TextBlock ProfileState(string text)
    {
        var line = U.Text(text, null, true);
        line.FontSize = 13;
        line.LineHeight = 19.5;
        line.Bind(TextBlock.ForegroundProperty, "profile-state");
        return line;
    }

    void Draw()
    {
        strips.Clear();
        Body.Children.Clear();
        var saveNew = Key(ActionButton(Studio.PreviewScene() == null ? "Save current as profile" : "Save preview as profile", Save, "primary"), "save-profile");
        saveNew.IsEnabled = loaded;
        undo = Key(ActionButton("Undo last apply", Undo), "undo-apply");
        undo.IsEnabled = Studio.CanUndoApply;
        Body.Children.Add(Header("Profiles", "Save a complete lighting setup and apply it when you want.",
            ActionButton("Refresh profiles", Reload), saveNew, undo));
        var list = U.Column(0);
        AutomationProperties.SetName(list, "Saved profiles");
        var detail = U.Column(18);
        detail.Margin = new Thickness(0, 0, 0, 20);
        var scenes = new JsonObject();
        foreach (var r in records.Objects()) if (r.Str("name") is { } key) scenes[key] = r["scene"].Copy();
        fingerprint = SecondaryModel.SceneFingerprint(state);
        var match = SecondaryModel.MatchingProfile(scenes, state);
        if (loaded) list.Children.Add(ProfileState(match != null ? $"Applied: {match}" : records.Count > 0 ? "Applied setup: Custom" : "Applied setup"));
        foreach (var record in records.Objects().Where(r => r.Str("name") is not null).OrderBy(r => r.Str("name"), StringComparer.CurrentCulture))
        {
            var key = record.Str("name")!;
            bool applied = key == match;
            var brightness = record.At("scene.brightness") is { } level && level.GetValueKind() != System.Text.Json.JsonValueKind.Null ? Js.ToStr(level) : "100";
            var item = ListItem(Strip(record["scene"], applied, compact: true), [ItemName(key), ItemLine($"{brightness}% brightness{(applied ? " · Applied" : "")}")],
                key == selected, () => { Act(() => { selected = key; Draw(); }); return Task.CompletedTask; });
            list.Children.Add(Key(item, "profile:" + key));
        }
        var current = records.Objects().FirstOrDefault(r => r.Str("name") == selected);
        if (failed) detail.Children.Add(LoadingPanel("Profiles could not be loaded. Your saved profiles are unchanged.", Reload));
        else if (!loaded) detail.Children.Add(LoadingPanel("Loading profiles…"));
        else if (current == null)
        {
            detail.Children.Add(U.H2("Save your first profile"));
            detail.Children.Add(U.P("Choose Save current as profile to keep your applied lighting, or Save preview as profile to keep unapplied changes.", "muted"));
        }
        else DrawDetail(detail, current, current.Str("name") == match);
        Body.Children.Add(Split(list, detail));
        Body.Children.Add(NoticeBox);
        Paint();
        if (strips.Count > 0) animation.Start(); else animation.Stop();
    }

    void DrawDetail(StackPanel detail, JsonObject current, bool applied)
    {
        var scene = current.Obj("scene");
        var title = U.Text(current.Str("name"), "h2", true);
        detail.Children.Add(U.Column(0, title, ProfileState(applied ? "Applied profile" : "Saved profile")));
        detail.Children.Add(Strip(scene, applied));
        var layoutNotice = ProfilePreview.ProfileLayoutNotice(scene, state);
        if (layoutNotice.Length > 0) detail.Children.Add(HelpText(layoutNotice));
        var apply = ActionButton("Apply profile", () => Apply(current), "primary");
        apply.IsEnabled = layoutNotice.Length == 0;
        var replace = ActionButton("Replace with applied setup", () => Change(current, "replace"), null);
        if (SecondaryModel.ProfileNameError(current.Str("name")).Length > 0)
        {
            replace.IsEnabled = false;
            ToolTip.SetTip(replace, "Rename this profile before updating it.");
            ToolTip.SetShowOnDisabled(replace, true);
        }
        detail.Children.Add(U.Wrap(10, apply, replace, ActionButton("Rename", () => Change(current, "rename")),
            ActionButton("Duplicate", () => Change(current, "duplicate")), ActionButton("Delete", () => Change(current, "delete"), "quiet danger")));

        // div.scene-summary
        var summary = new StackPanel { Margin = new Thickness(0, 8) };
        TextBlock Heading(string text, bool first)
        {
            var heading = U.Text(text, "h3 accent");
            heading.Margin = new Thickness(0, first ? 0 : 15, 0, 5);
            return heading;
        }
        summary.Children.Add(Heading("Ethernet groups", true));
        // Every .scene-group keeps its hairline: the summary's closing line follows the last one, so none is a last child.
        foreach (var group in scene.Objects("zones"))
        {
            string effectId = group.Str("effect.id") ?? "static";
            string? effectName = Workspace.Effects.Objects().FirstOrDefault(e => e.Str("id") == effectId)?.Str("name") ?? EffectRenderer.EffectById(effectId).Str("name");
            var ports = string.Join(", ", group.Arr("ports").Select(port => Js.ToStr(port)));
            var name = U.Text(group.Str("name"), null, true);
            name.FontWeight = FontWeight.Bold;
            name.FontSize = 13;
            var line = U.P($"{effectName} · Ports {ports}", "muted");
            line.FontSize = 13;
            summary.Children.Add(new Border
            {
                Child = U.Column(5, name, line), Padding = new Thickness(0, 12), BorderThickness = new Thickness(0, 0, 0, 1),
            }.Bind(Border.BorderBrushProperty, "line"));
        }
        var overrides = scene["overrides"] as JsonObject;
        var keys = Js.Keys(overrides);
        if (keys.Count > 0) summary.Children.Add(Heading("Individual port colors", false));
        foreach (var port in keys)
        {
            var value = overrides![port];
            var color = value.Str("color") ?? "";
            var level = value?["brightness"] is { } brightness && brightness.GetValueKind() != System.Text.Json.JsonValueKind.Null ? $"{Js.ToStr(brightness)}% brightness" : "Default brightness";
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), Background = U.Brush(color), VerticalAlignment = VerticalAlignment.Center }
                .Bind(Border.BorderBrushProperty, "swatch-highlight");
            var text = U.Text($"Port {port} · {color.ToUpperInvariant()} · {level}", "muted", true);
            text.FontSize = 13;
            summary.Children.Add(U.Row(10, swatch, text));
        }
        var sfp = SecondaryModel.ProfileSfpSummary(scene);
        if (sfp.Count > 0) summary.Children.Add(Heading("SFP lights", false));
        foreach (var s in sfp.Objects())
        {
            var light = U.P($"SFP {Js.ToStr(s["port"])} · {s.Str("label")}", "muted");
            light.Margin = new Thickness(0, 10, 0, 0);
            summary.Children.Add(light);
        }
        var count = scene.Arr("rules").Count;
        var rulesLine = U.P($"{count} {(count == 1 ? "rule" : "rules")} saved with this profile.", "muted");
        rulesLine.Margin = new Thickness(0, 10, 0, 0);
        summary.Children.Add(rulesLine);
        detail.Children.Add(summary);
    }

    Control Strip(JsonNode? scene, bool live, bool compact = false)
    {
        var row = U.Wrap(compact ? 4 : 5);
        row.MinHeight = 32;
        row.Margin = new Thickness(0, 5);
        var layout = live ? PortTopology.LightingPorts(state) : ProfilePreview.ProfilePorts(scene, state);
        var bars = new Dictionary<int, Border>();
        foreach (var id in layout.Copper.Concat(layout.Sfp))
        {
            var bar = new Border { Width = compact ? 12 : 13, Height = 24, CornerRadius = new CornerRadius(3) };
            if (layout.Copper.Count > 0 && layout.Sfp.FirstOrDefault() == id) bar.Margin = new Thickness(7, 0, 0, 0);
            bars[id] = bar;
            row.Children.Add(bar);
        }
        var colors = new Dictionary<string, ProfileAnimation>();
        ScrollViewer? viewport = null;
        strips.Add(time =>
        {
            if (!row.IsEffectivelyVisible || row.Bounds.Height == 0) return;
            viewport ??= row.FindAncestorOfType<ScrollViewer>();
            if (viewport != null && row.TranslatePoint(new Point(), viewport) is { } origin &&
                (origin.Y + row.Bounds.Height < 0 || origin.Y > viewport.Bounds.Height)) return;
            var snapshot = live ? state.Objects("ports").Concat(state.Objects("sfp")).Where(p => p.Int("port") != null)
                .ToDictionary(p => p.Int("port")!.Value, p => PortDisplay.PortDisplayRgb(p, state.Num("output.gamma")))
                : ProfilePreview.ProfileColors(scene, state, time, colors);
            foreach (var (id, bar) in bars)
            {
                string hex = snapshot.TryGetValue(id, out var rgb) ? ColorMath.RgbToHex(rgb) : "#000000";
                if (!Equals(bar.Tag, hex)) { bar.Tag = hex; bar.Background = U.Brush(hex); }
            }
        });
        return row;
    }

    void Paint()
    {
        if (!Active || Shell.Hidden || !IsEffectivelyVisible) return;
        foreach (var strip in strips) strip(elapsed.Elapsed.TotalSeconds);
    }

    public override void Update()
    {
        if (!Active || !loaded || Busy) return;
        state = Workspace.State;
        if (undo != null) undo.IsEnabled = Studio.CanUndoApply;
        if (fingerprint != SecondaryModel.SceneFingerprint(state)) Act(Draw);
    }

    public override void Frame() { if (loaded && Active) state = Workspace.State; }
    public override void VisibilityChanged(bool visible) { if (visible && loaded && strips.Count > 0) animation.Start(); else animation.Stop(); }
    public override void Leave() { animation.Stop(); strips.Clear(); base.Leave(); }
}
