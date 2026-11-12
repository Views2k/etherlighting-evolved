using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Ui;
using PM = EtherlightingEvolved.Native.Core.Model.PortMap;
using LS = EtherlightingEvolved.Native.Core.Model.LightingState;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Lighting;

public sealed partial class LightingPage
{
    sealed class Form(LightingPage page, string title, double width = 560) : DialogForm(page.Shell, title, width) { }
    Task FindPort()
    {
        var form = new Form(this, "Find a port"); var list = U.Column(6); var query = U.Input("", "Port number or device name");
        void Paint()
        {
            var words = (query.Text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            list.Children.Clear();
            foreach (var port in Ports)
            {
                if (port.Int("port") is not { } id) continue; bool sfp = editor.Optical.Contains(id);
                var name = PM.PortLabel(port, sfp).Str("text"); var type = sfp ? "SFP" : "Port";
                var hay = $"Port {id} {type} {id} {name} {port.Str("name")} {port.Str("label")} {port.Str("controller.name")} " + string.Join(' ', port.Objects("controller.clients").Select(c => c.Str("name")));
                if (words.Any(w => !hay.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
                list.Children.Add(form.Btn($"{type} {id} · {name} · {PM.LinkStatus(port).Str("label")}", async () => { if (await editor.SelectFromMap([id])) form.Dialog.Close(); }));
            }
            if (list.Children.Count == 0) list.Children.Add(U.P("No matching ports.", "muted"));
        }
        query.TextChanged += (_, _) => Paint(); form.Body.Children.Add(U.Field("Search ports and devices", query)); form.Body.Children.Add(list); form.Done(); Paint(); query.Focus(); return Task.CompletedTask;
    }
    async Task Groups()
    {
        if (!await editor.ConfirmDraft()) return;
        var send = Workspace.WorkspaceRequest(); var before = (await send("/api/scene", "GET", null)).Obj();
        var form = new Form(this, "Port groups"); form.Body.Children.Add(U.P("Select Ethernet or SFP ports on the map to create a group.", "muted")); form.Body.Children.Add(U.H3("Existing groups"));
        var names = new Dictionary<string, TextBox>();
        foreach (var zone in before.Objects("scene.zones"))
        {
            var input = U.Input(zone.Str("name")); input.MaxLength = PM.GroupNameLimit; names[zone.Str("id")!] = input;
            form.Body.Children.Add(U.Field("Group name", input, "Ports " + PortTopology.PortRanges(zone["ports"])));
        }
        var selected = editor.Selected.ToArray(); var fresh = U.Input("", "Group name"); fresh.MaxLength = PM.GroupNameLimit; fresh.IsEnabled = selected.Length > 0;
        var options = Workspace.Effects.Objects().Select(e => (e.Str("id")!, e.Str("name")!)).ToList();
        options.Insert(0, ("", CurrentEffect() is null ? "Choose an effect" : "Keep selected effect"));
        var fx = U.Select(options, ""); fx.IsEnabled = fresh.IsEnabled;
        form.Body.Children.Add(U.H3("New group")); form.Body.Children.Add(U.Field("Group for " + editor.Scope(), fresh)); form.Body.Children.Add(U.Field("Group effect", fx)); form.Body.Children.Add(U.P("Creating a group moves the selected ports into it. The chosen effect starts when you save.", "muted"));
        form.Done("Cancel"); form.Buttons.Children.Add(form.Btn("Save groups", async () =>
        {
            var zones = (JsonArray)before.Arr("scene.zones").DeepClone();
            foreach (var zone in zones.Objects())
            {
                var name = names[zone.Str("id")!].Text?.Trim() ?? ""; var err = PM.GroupNameError(name); if (err.Length > 0) throw new InvalidOperationException(err); zone["name"] = name;
            }
            if (fresh.Text?.Trim() is { Length: > 0 } nameNew)
            {
                var err = PM.GroupNameError(nameNew); if (err.Length > 0) throw new InvalidOperationException(err);
                var effect = fx.Value() is { Length: > 0 } chosen ? EffectRenderer.DefaultConfig(chosen) : CurrentEffect();
                if (effect is null) throw new InvalidOperationException("Choose the effect for the new group.");
                foreach (var zone in zones.Objects()) zone["ports"] = Js.NumberArray(PortTopology.PortIds(zone["ports"]).Except(selected));
                zones.Add(new JsonObject { ["id"] = "group-" + Guid.NewGuid(), ["name"] = nameNew, ["ports"] = Js.NumberArray(selected), ["effect"] = effect.DeepClone() });
            }
            var scene = Js.Spread(before["scene"]); scene["zones"] = Js.ArrayOf(zones.Objects().Where(z => z.Arr("ports").Count > 0));
            await Workspace.Serialize(async () => { await SceneTransaction.CommitScene(before, scene, send); await Workspace.Refresh(); });
            form.Dialog.Close(); Shell.Notify("Port groups saved");
        }, "primary"));
    }
    async Task History()
    {
        if (!await editor.ConfirmDraft()) return;
        var send = Workspace.WorkspaceRequest(); var before = (await send("/api/scene", "GET", null)).Obj(); var form = new Form(this, "Lighting history"); form.Done();
        await form.Do(async () =>
        {
            var result = await send("/api/history", "GET", null); var entries = result.At("entries") ?? result.At("history");
            if (result.Str("error") is { } message) form.Body.Children.Add(U.P(message, "error"));
            foreach (var entry in entries.Objects())
            {
                var date = entry.Str("at") ?? entry.Str("created_at");
                var time = DateTimeOffset.TryParse(date, out var when) ? when.ToLocalTime().ToString("g") : "Saved setup";
                var description = entry.Str("summary") ?? ($"{entry.Num("scene.brightness") ?? 100}% brightness · " + string.Join(" / ", entry.Objects("scene.zones").Select(z => $"{z.Str("name")} · {Label(z.Str("effect.id") ?? "")} · Ports {PortTopology.PortRanges(z["ports"])}")));
                form.Body.Children.Add(U.Column(6, U.H3(time), U.P(description, "muted"), form.Btn("Restore", async () =>
                {
                    if (!await Shell.Overlay.Confirm("Restore this setup?", "Apply this saved lighting setup to the connected switch.", "Restore")) return;
                    await Workspace.Serialize(async () => { await send("/api/history/" + Uri.EscapeDataString(entry.Str("id")!) + "/restore", "POST", new JsonObject { ["expected_revision"] = before["revision"].Copy() }); await Workspace.Refresh(); });
                    form.Dialog.Close(); Shell.Notify("Lighting setup restored");
                })));
            }
            if (!entries.Objects().Any()) form.Body.Children.Add(U.P("Lighting changes will appear here.", "muted"));
        });
    }
    async Task Library(string kind)
    {
        var selectionLibrary = kind == "selection"; var path = "/api/library/" + kind; var send = Workspace.WorkspaceRequest();
        var form = new Form(this, selectionLibrary ? "Saved selections" : "Saved effect settings"); var name = U.Input("", "Name"); name.MaxLength = 64;
        var list = U.Column(10); var change = U.Wrap(5); JsonObject? editing = null; var replace = false;
        JsonObject? Value() => selectionLibrary ? new JsonObject { ["ports"] = Js.NumberArray(editor.Selected) } : CurrentEffect() is { } current ? new JsonObject { ["id"] = current["id"].Copy(), ["params"] = current["params"].Copy() } : null;
        void SetEditing(JsonObject? record, bool replacing)
        {
            editing = record; replace = replacing; name.Text = editing.Str("name") ?? ""; change.Children.Clear();
            change.Children.Add(form.Btn(editing is null ? (selectionLibrary ? "Save current selection" : "Save current settings") : replace ? "Replace saved settings" : "Save name", Save, "primary"));
            if (editing is not null) { change.Children.Add(U.Button("Cancel", "quiet", () => SetEditing(null, false))); name.Focus(); }
        }
        async Task Save()
        {
            var value = editing is not null && !replace ? editing["value"]?.DeepClone().AsObject() : Value();
            if (value is null) throw new InvalidOperationException("Choose effect settings first.");
            if (string.IsNullOrWhiteSpace(name.Text)) throw new InvalidOperationException("Enter a name.");
            if (selectionLibrary) { value.Remove("topology"); if (value.Arr("ports").Count == 0) throw new InvalidOperationException("Select ports first."); }
            var payload = new JsonObject { ["name"] = name.Text.Trim(), ["value"] = value }; if (editing is not null) payload["expected_revision"] = editing["revision"].Copy();
            await send(path + (editing is null ? "" : "/" + Uri.EscapeDataString(editing.Str("id")!)), editing is null ? "POST" : "PUT", payload); SetEditing(null, false); await Load();
        }
        async Task Load()
        {
            var records = (await send(path, "GET", null)).Objects("items").ToList(); list.Children.Clear();
            foreach (var record in records)
            {
                var value = record.Obj("value"); var fits = !selectionLibrary || PortTopology.SceneTopology(value)?.Same(PortTopology.LightingPorts(State)) == true;
                var use = form.Btn(selectionLibrary ? "Select ports" : "Preview settings", async () =>
                {
                    if (selectionLibrary) { if (!await editor.SelectFromMap(PortTopology.PortIds(value["ports"]))) return; } else await editor.ChooseEffect(value);
                    form.Dialog.Close();
                }); use.IsEnabled = fits;
                list.Children.Add(U.Column(6, U.H3(record.Str("name") ?? ""), U.P((selectionLibrary ? "Ports " + PortTopology.PortRanges(value["ports"]) : Label(value.Str("id") ?? "")) + (fits ? "" : " · Different switch layout"), "muted"), U.Wrap(6, use,
                    U.Button("Rename", "quiet", () => SetEditing(record, false)), U.Button("Replace", "quiet", () => SetEditing(record, true)), form.Btn("Delete", async () =>
                    {
                        if (!await Shell.Overlay.Confirm($"Delete {record.Str("name")}?", "Remove this saved " + (selectionLibrary ? "port selection." : "effect configuration."), "Delete")) return;
                        await send(path + "/" + Uri.EscapeDataString(record.Str("id")!), "DELETE", new JsonObject { ["expected_revision"] = record["revision"].Copy() }); SetEditing(null, false); await Load();
                    }, "danger"))));
            }
            if (records.Count == 0) list.Children.Add(U.P(selectionLibrary ? "Save selected ports to recall them together." : "Save these settings to use them on another selection.", "muted"));
        }
        form.Body.Children.Add(U.Field("Name", name)); form.Body.Children.Add(change); form.Body.Children.Add(list); form.Done(); SetEditing(null, false); await form.Do(Load);
    }
    async Task PortNames(IReadOnlyList<int> ports)
    {
        var send = Workspace.WorkspaceRequest(); var form = new Form(this, "Port names"); JsonObject record = new();
        var labels = new Dictionary<int, TextBox>(); var manual = new JsonObject(); bool painting = false;
        var toggle = U.Check("Use UniFi port names"); var note = U.P("Connect UniFi in Settings to load its port names.", "muted");
        form.Body.Children.Add(toggle); form.Body.Children.Add(U.P("Applies to all ports on this switch. Your manual names are saved separately.", "muted")); form.Body.Children.Add(note);
        foreach (var p in ports)
        {
            var field = U.Input(); field.MaxLength = 64; var n = p; AutomationProperties.SetName(field, $"Port {p} name"); field.TextChanged += (_, _) => { if (!painting && record["revision"] is not null && toggle.IsChecked != true) manual[n.ToString()] = field.Text; }; labels[p] = field;
            form.Body.Children.Add(U.Field($"{(editor.Optical.Contains(p) ? "SFP" : "Port")} {p}", field));
        }
        Button? save = null;
        void Paint()
        {
            painting = true; try
            {
                toggle.IsEnabled = record["revision"] is not null; if (save is not null) save.IsEnabled = toggle.IsEnabled;
                note.IsVisible = toggle.IsChecked == true && State.Bool("controller.connected") != true;
                foreach (var (id, input) in labels)
                {
                    var p = Ports.FirstOrDefault(p => p.Int("port") == id); input.IsEnabled = toggle.IsEnabled && toggle.IsChecked != true;
                    input.Text = toggle.IsChecked == true ? PM.PortLabel(Js.Spread(p, new JsonObject { ["name_mode"] = "unifi", ["display_name"] = p.At("controller.port_name").Copy() }), editor.Optical.Contains(id)).Str("text") : manual.Str(id.ToString()) ?? "";
                }
            }
            finally { painting = false; }
        }
        async Task Load() { record = (await send("/api/port-names", "GET", null)).Obj(); manual = (JsonObject)record.Obj("names").DeepClone(); toggle.IsChecked = record.Bool("use_unifi") == true; Paint(); }
        toggle.IsCheckedChanged += (_, _) => Paint();
        form.Buttons.Children.Add(form.Btn("Reload names", Load)); form.Done("Cancel");
        save = form.Btn("Save names", async () =>
        {
            if (record["revision"] is null) throw new InvalidOperationException("Reload the port names before saving.");
            var names = new JsonObject(); foreach (var id in ports) names[id.ToString()] = manual.Str(id.ToString()) ?? "";
            await send("/api/port-names", "PUT", new JsonObject { ["expected_revision"] = record["revision"].Copy(), ["use_unifi"] = toggle.IsChecked == true, ["names"] = names });
            await Workspace.Refresh(false); form.Dialog.Close(); Shell.Notify("Port names saved");
        }, "primary"); form.Buttons.Children.Add(save);
        Paint(); await form.Do(Load);
    }
    sealed record TrafficPoint(DateTimeOffset At, double? Value, int Segment);
    sealed class TrafficHistory { public Queue<TrafficPoint> Samples = new(); public List<string> Events = []; public string? Link; public bool? Online; }
    readonly Dictionary<int, TrafficHistory> history = [];
    string? generation, sampleIdentity; bool continuity; int segment; long sampleArrival;
    void RecordHistory()
    {
        if (Workspace.Disconnected || !Workspace.Connected || State.Bool("telemetry.stale") == true) { continuity = false; return; }
        var gen = State.At("telemetry.generation")?.ToString(); var seq = State.At("telemetry.sequence")?.ToString(); var key = gen + ":" + seq;
        if (gen is null || seq is null || key == sampleIdentity || !DateTimeOffset.TryParse(State.Str("telemetry.sampled_at"), out var at)) return;
        bool next = continuity && gen == generation; if (!next) segment++;
        generation = gen; sampleIdentity = key; sampleArrival = watch.ElapsedMilliseconds; continuity = true;
        foreach (var p in Ports)
        {
            if (p.Int("port") is not { } id) continue; if (!history.TryGetValue(id, out var h)) history[id] = h = new();
            var link = PM.LinkStatus(p); var label = link.Str("label"); var online = link.Bool("online");
            if (next && label != h.Link && online.HasValue && h.Online.HasValue) h.Events.Insert(0, $"{at.LocalDateTime:t} · {h.Link} → {label}");
            if (!next && h.Samples.Count > 0) h.Events.Insert(0, $"{at.LocalDateTime:t} · Monitoring resumed");
            if (h.Events.Count > 12) h.Events.RemoveRange(12, h.Events.Count - 12); h.Link = label; h.Online = online;
            h.Samples.Enqueue(new(at, p.Num("facts.traffic"), segment)); while (h.Samples.TryPeek(out var s) && (h.Samples.Count > 120 || s.At < at.AddMinutes(-1))) h.Samples.Dequeue();
        }
    }
    Task PortDetails()
    {
        var ports = editor.Selected.ToArray(); if (ports.Length == 0) return Task.CompletedTask;
        var send = Workspace.WorkspaceRequest(); var form = new Form(this, "Port details", 680);
        var painters = new List<Action<bool>>();
        foreach (var id in ports)
        {
            var caption = U.H3(""); var fields = U.Column(3); var facts = new Dictionary<string, TextBlock>();
            foreach (var f in new[] { "Link", "Traffic", "Power", "Lighting", "Sample", "Devices" }) { var value = U.Text("", "muted", true); fields.Children.Add(U.Wrap(14, U.Text(f), value)); facts[f] = value; }
            var trends = U.P("", "muted"); var events = U.Text("", "small muted", true); var chart = new Canvas { Height = 44, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
            var identify = form.Btn("Identify for 10 seconds", async () =>
            {
                var active = State.At("identify");
                if (active.Int("port") == id) await send("/api/identify", "DELETE", new JsonObject { ["id"] = active.At("id").Copy() });
                else await send("/api/identify", "POST", new JsonObject { ["port"] = id }); await Workspace.Refresh(false);
            });
            form.Body.Children.Add(U.Column(8, caption, fields, chart, trends, events, identify, U.Divider()));
            string? graphed = null;
            painters.Add(stale =>
            {
                var p = Ports.FirstOrDefault(x => x.Int("port") == id); if (p is null) return; var sfp = editor.Optical.Contains(id);
                caption.Text = $"{(sfp ? "SFP" : "Port")} {id} · {PM.PortLabel(p, sfp).Str("text")}"; facts["Link"].Text = PM.LinkStatus(p).Str("label") + (PM.IsUplink(p) ? " · Uplink" : "");
                facts["Traffic"].Text = !stale && p.Num("facts.traffic") is { } t ? $"{t:0.00} Mbps · RX + TX" : "Waiting for a sample";
                facts["Power"].Text = sfp ? "—" : PM.PortPower(p).Str("description"); facts["Lighting"].Text = State.Int("identify.port") == id ? "Identifying" : LS.ControlSource(State, id, Workspace.Effects).Str("label");
                facts["Sample"].Text = stale ? "Monitoring interrupted" : DateTimeOffset.TryParse(State.Str("telemetry.sampled_at"), out var at) ? at.LocalDateTime.ToLongTimeString() : "Waiting for a sample";
                facts["Devices"].Text = string.Join(", ", p.Objects("controller.clients").Select(c => c.Str("name") ?? "Unnamed device"));
                var active = State.Int("identify.port") == id; identify.Content = active ? $"Stop identifying · {State.Num("identify.seconds_remaining"):0}s" : "Identify for 10 seconds";
                identify.IsEnabled = !stale && State.Str("mode") == "running" && State.Bool("reconnect_required") != true && (State["identify"] is null || active);
                if (graphed == sampleIdentity) return; graphed = sampleIdentity;
                if (!history.TryGetValue(id, out var h)) return; events.Text = string.Join("\n", h.Events); var pts = h.Samples.Where(x => x.Value >= 0).ToList(); chart.Children.Clear();
                if (pts.Count < 2) { trends.Text = "Waiting for traffic samples"; return; }
                var peak = pts.Max(x => x.Value!.Value);
                trends.Text = $"Last minute · Peak {peak:0.00} Mbps"; var now = pts[^1].At;
                foreach (var group in pts.GroupBy(x => x.Segment))
                {
                    var seg = new Polyline { Stroke = U.Brush("#6ba8e8"), StrokeThickness = 1.7 }; seg.Points = new Points(group.Select(x => new Point(300 * (1 - (now - x.At).TotalSeconds / 60), 42 - (x.Value ?? 0) / Math.Max(.01, peak) * 40)));
                    chart.Children.Add(seg);
                }
            });
        }
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        void Paint() { var stale = Workspace.Disconnected || !Workspace.Connected || State.Bool("telemetry.stale") == true || State.Num("telemetry.sample_age_ms") + watch.ElapsedMilliseconds - sampleArrival >= 10000; foreach (var action in painters) action(stale); }
        form.Buttons.Children.Add(form.Btn("Edit port names", () => PortNames(ports))); form.Done(); timer.Tick += (_, _) => { if (!Shell.Hidden) Paint(); }; Paint(); timer.Start();
        _ = Stop(); async Task Stop() { await form.Dialog.Result; timer.Stop(); }
        return Task.CompletedTask;
    }
}
