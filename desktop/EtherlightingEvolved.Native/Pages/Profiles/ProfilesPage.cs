using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Profiles;

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
    bool loaded;
    public ProfilesPage(AppShell shell) : base(shell)
    {
        animation.Tick += (_, _) => Paint();
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
            state = (JsonObject)Workspace.State.DeepClone();
            foreach (var entry in (await current).Obj()) state[entry.Key] = entry.Value.Copy();
            if (!records.Objects().Any(r => r.Str("name") == selected)) selected = records.FirstOrDefault().Str("name");
            loaded = true;
            Draw();
        }
        catch
        {
            if (!loaded && Active)
            {
                Body.Children.Clear();
                Body.Children.Add(U.EmptyState("Profiles could not be loaded", "Your saved profiles are unchanged.", ActionButton("Retry", Reload)));
                Body.Children.Add(Notice);
            }
            throw;
        }
    }

    async Task AfterWrite(string success)
    {
        try { await Workspace.Refresh(false); await Reload(); Tell(success); }
        catch { Tell($"{success} Status could not be refreshed. Use Refresh profiles to check it.", true); }
    }

    async Task<string?> AskName(string title, string initial = "", string? unchanged = null)
    {
        var field = U.Input(initial);
        field.MaxLength = 64;
        var error = U.P("", "error");
        var buttons = U.Wrap();
        var body = U.Column(14, U.H2(title), U.Field("Profile name", field), error, buttons);
        var dialog = Shell.Overlay.Show(body, width: 400, sheet: true);
        string? answer = null;
        void Accept()
        {
            string name = field.Text?.Trim() ?? "";
            string reason = SecondaryModel.ProfileNameError(name);
            if (reason.Length == 0 && name != unchanged && records.Objects().Any(r => r.Str("name") == name))
                reason = "A profile with this name already exists.";
            if (reason.Length > 0) { error.Text = reason; field.Focus(); return; }
            answer = name;
            dialog.Close();
        }
        buttons.Children.Add(U.Button("Cancel", null, () => dialog.Close()));
        buttons.Children.Add(U.Button(title, "primary", Accept));
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Accept(); } };
        field.Focus();
        field.SelectAll();
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
                if (next == null) return;
                if (action == "rename" && next == name) { Tell("Profile name unchanged."); return; }
                payload["name"] = next;
                message = action == "rename" ? "Profile renamed." : "Profile duplicated.";
                break;
            case "delete":
                var schedules = record.Objects("schedules").Where(s => s.Bool("enabled") == true).Select(s => $"“{s.Str("name")}”").ToList();
                var warning = schedules.Count > 0 ? $" This also disables {string.Join(", ", schedules)}." : "";
                if (!await Shell.Overlay.Confirm("Delete profile?", $"Remove “{name}” from your saved profiles? Your current lighting keeps running.{warning}", "Delete")) return;
                payload["disable_schedules"] = schedules.Count > 0;
                next = null;
                message = "Profile deleted.";
                break;
            default: return;
        }
        await Send("/api/profiles/" + Uri.EscapeDataString(record.Str("id")!), "PATCH", payload);
        selected = next;
        await AfterWrite(message);
    }

    void Draw()
    {
        strips.Clear();
        Body.Children.Clear();
        var undo = ActionButton("Undo last apply", async () => { await Studio.UndoLastApply(); await Reload(); });
        undo.IsEnabled = Studio.CanUndoApply;
        Body.Children.Add(Header("Profiles", "Save a complete lighting setup and apply it when you want.",
            ActionButton("Refresh profiles", Reload), ActionButton(Studio.PreviewScene() == null ? "Save current as profile" : "Save preview as profile", Save, "primary"), undo));
        var list = U.Column(7);
        var detail = U.Column(13);
        var scenes = new JsonObject();
        foreach (var r in records.Objects()) scenes[r.Str("name")!] = r["scene"].Copy();
        fingerprint = SecondaryModel.SceneFingerprint(state);
        var match = SecondaryModel.MatchingProfile(scenes, state);
        list.Children.Add(U.P(match != null ? $"Applied: {match}" : records.Count > 0 ? "Applied setup: Custom" : "Applied setup", "muted"));
        foreach (var record in records.Objects().OrderBy(r => r.Str("name"), StringComparer.CurrentCulture))
        {
            var key = record.Str("name")!;
            bool applied = key == match;
            var item = ListItem(key, $"{record.Num("scene.brightness") ?? 100}% brightness{(applied ? " · Applied" : "")}", key == selected,
                () => { selected = key; Draw(); return Task.CompletedTask; });
            ((StackPanel)item.Content!).Children.Insert(0, Strip(record["scene"], applied, compact: true));
            list.Children.Add(item);
        }
        var current = records.Objects().FirstOrDefault(r => r.Str("name") == selected);
        if (current == null)
        {
            detail.Children.Add(U.H2("Save your first profile"));
            detail.Children.Add(U.P("Choose Save current as profile to keep your applied lighting, or Save preview as profile to keep unapplied changes.", "muted"));
        }
        else
        {
            var scene = current.Obj("scene");
            detail.Children.Add(U.H2(current.Str("name")!));
            detail.Children.Add(U.P(selected == match ? "Applied profile" : "Saved profile", "muted"));
            detail.Children.Add(Strip(scene, selected == match));
            var layoutNotice = ProfilePreview.ProfileLayoutNotice(scene, state);
            if (layoutNotice.Length > 0) detail.Children.Add(U.P(layoutNotice, "muted"));
            var apply = ActionButton("Apply profile", async () => { await Studio.ApplyProfile(current); await Reload(); }, "primary");
            apply.IsEnabled = layoutNotice.Length == 0;
            var replace = ActionButton("Replace with applied setup", () => Change(current, "replace"));
            replace.IsEnabled = SecondaryModel.ProfileNameError(selected).Length == 0;
            detail.Children.Add(U.Wrap(6, apply, replace, ActionButton("Rename", () => Change(current, "rename")),
                ActionButton("Duplicate", () => Change(current, "duplicate")), ActionButton("Delete", () => Change(current, "delete"), "quiet danger")));
            detail.Children.Add(U.Divider());
            detail.Children.Add(U.H3("Ethernet groups"));
            foreach (var group in scene.Objects("zones"))
            {
                string effectId = group.Str("effect.id") ?? "static";
                string? effectName = Workspace.Effects.Objects().FirstOrDefault(e => e.Str("id") == effectId).Str("name") ?? EffectRenderer.EffectById(effectId).Str("name");
                detail.Children.Add(U.Column(2, U.Text(group.Str("name"), "strong"), U.P($"{effectName} · Ports {PortTopology.PortRanges(group["ports"])}", "muted")));
            }
            if (scene.Obj("overrides").Count > 0) detail.Children.Add(U.H3("Individual port colors"));
            foreach (var (port, value) in scene.Obj("overrides"))
            {
                string color = value.Str("color")?.ToUpperInvariant() ?? "";
                var brightness = value.Int("brightness");
                detail.Children.Add(U.Row(7, new Border { Width = 12, Height = 12, Background = U.Brush(color), CornerRadius = new CornerRadius(3) },
                    U.P($"Port {port} · {color} · {(brightness is null ? "Default brightness" : $"{brightness}% brightness")}", "muted")));
            }
            var sfp = SecondaryModel.ProfileSfpSummary(scene);
            if (sfp.Count > 0) detail.Children.Add(U.H3("SFP lights"));
            foreach (var s in sfp.Objects()) detail.Children.Add(U.P($"SFP {s.Int("port")} · {s.Str("label")}", "muted"));
            var count = scene.Arr("rules").Count;
            detail.Children.Add(U.P($"{count} {(count == 1 ? "rule" : "rules")} saved with this profile.", "muted"));
        }
        Body.Children.Add(Split(list, detail));
        Body.Children.Add(Notice);
        Paint();
        if (strips.Count > 0) animation.Start(); else animation.Stop();
    }

    Control Strip(JsonNode? scene, bool live, bool compact = false)
    {
        var row = U.Wrap(compact ? 4 : 5);
        row.MinHeight = 32;
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
        if (fingerprint != SecondaryModel.SceneFingerprint(state)) Draw();
    }
    public override void Frame() { if (loaded && Active) state = Workspace.State; }
    public override void VisibilityChanged(bool visible) { if (visible && loaded && strips.Count > 0) animation.Start(); else animation.Stop(); }
    public override void Leave() { animation.Stop(); strips.Clear(); base.Leave(); }
}
