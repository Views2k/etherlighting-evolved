using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

internal sealed partial class AutomationPage : ManagementPage
{
    [ModuleInitializer] internal static void Register() => PageRegistry.Register("Automation", shell => new AutomationPage(shell));
    static readonly (string Value, string Label)[] RuleStyles = [("solid", "Solid color"), ("pulse", "Gentle pulse"), ("blink", "Blink"), ("flash", "Flash"), ("effect", "Use port effect")];
    JsonArray specs = [], templates = [], rules = [];
    JsonObject baseline = [], draft = [];
    string? selected, observedRevision;
    string section = "When";
    bool schedulesTab, edited, reordered, loaded;
    readonly Dictionary<string, TextBlock> outputLabels = [];
    long nextOutputUpdate;
    TextBlock? summary;
    Button? apply;
    PortLayout Ports => PortTopology.LightingPorts(Workspace.State);
    public AutomationPage(AppShell shell) : base(shell)
    {
        selected = Studio.RequestedRuleId;
        Studio.RequestedRuleId = null;
        Start(Load);
    }

    JsonObject Blank()
    {
        var ports = Ports;
        return new JsonObject
        {
            ["name"] = "",
            ["enabled"] = true,
            ["match"] = "all",
            ["ports"] = Js.NumberArray(ports.Copper.Count > 0 ? ports.Copper : ports.Sfp),
            ["conditions"] = new JsonArray(),
            ["look"] = new JsonObject { ["style"] = "solid", ["color"] = "#ff667e", ["rate"] = .25, ["brightness"] = null }
        };
    }
    public override bool HasChanges => edited || reordered;
    Task<bool> Discard(bool includeOrder = false) => !edited && (!includeOrder || !reordered) ? Task.FromResult(true)
        : Shell.Overlay.Confirm("Discard changes?", schedulesTab ? "Your unapplied schedule changes will be discarded." :
            includeOrder ? "Your unapplied rule and priority changes will be discarded." : "Your unapplied rule changes will be discarded.", "Discard");
    public override async Task<bool> CanLeave() => !Busy && await Discard(true);
    async Task Load()
    {
        try
        {
            if (schedulesTab) await LoadSchedules();
            else
            {
                var fields = Get("/api/rules/fields");
                var examples = Get("/api/rules/templates");
                var scene = Get("/api/scene");
                await Task.WhenAll(fields, examples, scene);
                if (!Active) return;
                specs = (await fields).Arr("fields");
                templates = (await examples).Arr();
                AcceptRules((await scene).Obj());
                selected = rules.Objects().FirstOrDefault(r => r.Str("id") == selected)?.Str("id") ?? rules.FirstOrDefault().Str("id");
                ResetRule();
                observedRevision = Workspace.State["revision"]?.ToString();
            }
            if (!Active) return;
            loaded = true;
            edited = reordered = false;
            Draw();
        }
        catch
        {
            if (Active && !loaded)
            {
                Body.Children.Clear();
                Body.Children.Add(Tabs());
                Body.Children.Add(U.EmptyState(schedulesTab ? "Schedules could not be loaded" : "Rules could not be loaded",
                    "Refresh to try again.", ActionButton("Retry", Load)));
                Body.Children.Add(Notice);
            }
            throw;
        }
    }
    void AcceptRules(JsonObject scene)
    {
        if (scene.At("scene.rules") is not JsonArray rows || scene["revision"] == null)
            throw new InvalidOperationException("Refresh the rules before applying changes.");
        baseline = scene;
        rules = (JsonArray)rows.DeepClone();
    }
    void ResetRule()
    {
        draft = rules.Objects().FirstOrDefault(r => r.Str("id") == selected)?.DeepClone().AsObject() ?? Blank();
        if (draft["ports"] == null) draft["ports"] = Js.NumberArray(Ports.Copper);
    }
    async Task Go(bool schedules)
    {
        if (schedules == schedulesTab || !await Discard(true)) return;
        schedulesTab = schedules;
        loaded = edited = reordered = false;
        Tell("");
        await Load();
    }
    Control Tabs() => U.Wrap(6, ActionButton("Rules", () => Go(false), !schedulesTab ? "selected" : "quiet"),
        ActionButton("Schedules", () => Go(true), schedulesTab ? "selected" : "quiet"));
    void Draw()
    {
        Body.Children.Clear(); outputLabels.Clear(); summary = null; apply = null;
        if (schedulesTab) DrawSchedules();
        else DrawRules();
        Body.Children.Insert(1, Tabs());
        Body.Children.Add(Notice);
    }
    async Task SelectRule(string? id)
    {
        if (!await Discard()) return;
        selected = id; edited = false; ResetRule(); Draw();
    }
    void DrawRules()
    {
        Body.Children.Add(Header("Automation", "Choose when your ports change color.",
            ActionButton("Refresh rules", async () => { if (await Discard(true)) { Tell(""); await Load(); } }),
            ActionButton("New rule", () => SelectRule(null), "primary")));
        var list = U.Column(8);
        var ports = Ports;
        int index = 0;
        foreach (var r in rules.Objects())
        {
            string id = r.Str("id")!;
            var item = ListItem($"{++index}. {r.Str("name")}", $"{(r.Bool("enabled") == true ? "Enabled" : "Disabled")} · {SecondaryModel.ScopeSummary(r["ports"], ports.Copper, ports.Sfp)}",
                id == selected, () => Run(() => SelectRule(id)));
            var stack = (StackPanel)item.Content!;
            var title = (TextBlock)stack.Children[0];
            stack.Children.RemoveAt(0);
            stack.Children.Insert(0, U.Row(8, new Border { Width = 12, Height = 12, CornerRadius = new Avalonia.CornerRadius(4), Background = U.Brush(r.Str("look.color") ?? "#606977") }, title));
            var label = U.P("", "muted"); outputLabels[id] = label; stack.Children.Add(label);
            list.Children.Add(item);
        }
        if (rules.Count == 0) list.Children.Add(U.P("Create a rule to respond to port activity or time.", "muted"));
        list.Children.Add(U.P("Rules run from top to bottom. The first match sets the port lighting.", "muted"));
        if (reordered)
        {
            list.Children.Add(U.H3("Priority changes"));
            list.Children.Add(U.Wrap(8, ActionButton("Apply priority", SavePriority, "primary"), U.Button("Cancel priority", null, () =>
            { rules = (JsonArray)baseline.Arr("scene.rules").DeepClone(); reordered = false; Draw(); })));
        }
        Body.Children.Add(Split(list, RuleEditor()));
        UpdateLabels();
    }

    void Changed() { edited = true; UpdateSummary(); }
    void UpdateSummary()
    {
        int count = draft.Arr("conditions").Count;
        var ports = Ports;
        if (summary != null) summary.Text = $"{SecondaryModel.ScopeSummary(draft["ports"], ports.Copper, ports.Sfp)} · {RuleStyles.FirstOrDefault(s => s.Value == draft.Str("look.style")).Label} · {count} {(count == 1 ? "condition" : "conditions")}";
        if (apply != null) apply.IsEnabled = RuleError().Length == 0;
    }
    string RuleError()
    {
        var reason = SecondaryModel.ValidateRuleDraft(draft, specs);
        if (reason.Length > 0) return reason;
        if (draft.Str("look.style") != "effect" && ColorMath.NormalizeHex(draft.Str("look.color")) == null) return "Enter a valid color.";
        foreach (var c in draft.Objects("conditions"))
        {
            var spec = specs.Objects().First(s => s.Str("key") == c.Str("field"));
            if (spec.Str("type") == "number" && (c.Str("op") == "between" ? c.Arr("value").Count != 2 || c.Arr("value").Any(n => n.Num() == null) : c.Num("value") == null))
                return "Enter a number for each threshold.";
            if (spec.Str("type") == "time" && (c.Arr("value").Count != 2 || Values(c["value"]).Any(s => !TimeOnly.TryParseExact(s, "HH:mm", null, System.Globalization.DateTimeStyles.None, out _))))
                return "Enter a start and end time.";
        }
        return "";
    }
    JsonArray Candidate(string id)
    {
        var next = (JsonArray)rules.DeepClone();
        var payload = (JsonObject)draft.DeepClone();
        payload["id"] = id; payload["name"] = draft.Str("name")?.Trim();
        var index = next.IndexOf(next.Objects().FirstOrDefault(r => r.Str("id") == id));
        if (index < 0) next.Add(payload); else next[index] = payload;
        return next;
    }
    async Task<bool> BeforeWrite()
    {
        bool hadDraft = Studio.HasEdits;
        if (!await Studio.ConfirmDraft()) return false;
        if (hadDraft)
        {
            var now = (await Get("/api/scene")).Obj();
            if (!JsonNode.DeepEquals(now.At("scene.rules"), baseline.At("scene.rules")))
                throw new InvalidOperationException("The rules changed while the preview was resolved. Your edits are kept. Refresh rules before applying.");
            baseline = now;
        }
        return true;
    }
    async Task<bool> WriteRules(JsonArray next)
    {
        var scene = SecondaryModel.RulesSnapshot(baseline["scene"], next);
        try { AcceptRules((await SceneTransaction.CommitScene(baseline, scene, Send)).Obj()); return true; }
        catch (Exception ex) when (SceneTransaction.AcceptedScene(ex) is JsonObject accepted) { AcceptRules(accepted); return false; }
    }
    async Task FinishWrite(string success, bool confirmed)
    {
        bool refreshed = true;
        try { await Workspace.Refresh(false); observedRevision = Workspace.State["revision"]?.ToString(); } catch { refreshed = false; }
        if (!Active) return;
        Draw();
        Tell(!confirmed ? "Rule changes are saved, but lighting output was not confirmed. Use Refresh rules to check the current state." :
            refreshed ? success : success + " Use Refresh rules to check the current state.", !confirmed || !refreshed);
    }
    async Task SaveRule()
    {
        var error = RuleError();
        if (error.Length != 0) { Tell(error, true); return; }
        if (!await BeforeWrite()) return;
        var id = selected ?? Guid.NewGuid().ToString("N");
        var confirmed = await WriteRules(Candidate(id));
        selected = id; ResetRule(); reordered = edited = false;
        await FinishWrite("Rule applied.", confirmed);
    }
    async Task SavePriority()
    {
        if (!await BeforeWrite()) return;
        var confirmed = await WriteRules(rules);
        reordered = false;
        await FinishWrite("Rule priority applied.", confirmed);
    }
    async Task DeleteRule()
    {
        if (!await BeforeWrite() || !await Shell.Overlay.Confirm("Delete rule?", "This rule will be removed from your lighting setup.", "Delete")) return;
        var order = rules.Objects().Where(r => r.Str("id") != selected).Select(r => r.Str("id")).ToList();
        var next = new JsonArray(baseline.Objects("scene.rules").Where(r => r.Str("id") != selected).Select(r => r.Copy()).ToArray());
        var confirmed = await WriteRules(next);
        if (reordered)
        {
            rules = new JsonArray(rules.Objects().OrderBy(r => order.IndexOf(r.Str("id"))).Select(r => r.Copy()).ToArray());
            reordered = !JsonNode.DeepEquals(rules, baseline.At("scene.rules"));
        }
        selected = rules.FirstOrDefault().Str("id"); ResetRule(); edited = false;
        await FinishWrite("Rule deleted.", confirmed);
    }
    void UpdateLabels()
    {
        if (outputLabels.Count == 0) return;
        var st = Workspace.State;
        var counts = new Dictionary<string, int>();
        bool available = st.Objects("ports").Any(p => p["source"] != null) || st.Objects("sfp").Any(p => p.ContainsKey("rule"));
        foreach (var id in st.Objects("ports").Where(p => p.Str("source.kind") == "rule").Select(p => p.Str("source.id"))
            .Concat(st.Objects("sfp").Select(p => p.Str("rule.id"))).OfType<string>()) counts[id] = counts.GetValueOrDefault(id) + 1;
        foreach (var (id, label) in outputLabels)
        {
            int n = counts.GetValueOrDefault(id);
            label.Text = st.Str("mode") != "running" ? "EE output is stopped" : !available ? "Waiting for output status" : $"Controlling {n} {(n == 1 ? "port" : "ports")}";
        }
    }
    public override void Frame()
    {
        long now = Environment.TickCount64;
        if (Active && loaded && !schedulesTab && now >= nextOutputUpdate)
        { nextOutputUpdate = now + 500; UpdateLabels(); }
    }
    public override void Update()
    {
        if (!Active || !loaded || schedulesTab || Busy) return;
        var revision = Workspace.State["revision"]?.ToString();
        if (revision == null || revision == observedRevision) return;
        observedRevision = revision;
        if (HasChanges) Tell("The lighting setup changed. Refresh rules to review it before applying.", true);
        else _ = Run(Load);
    }
}
