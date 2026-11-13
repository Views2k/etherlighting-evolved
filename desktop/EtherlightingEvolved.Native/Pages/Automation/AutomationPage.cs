using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

/// <summary>Port of renderAutomation and renderRules (secondary.mjs:203-580).</summary>
internal sealed partial class AutomationPage : ManagementPage
{
    [ModuleInitializer] internal static void Register() => PageRegistry.Register("Automation", shell => new AutomationPage(shell));
    static readonly (string Value, string Label)[] RuleStyles = [("solid", "Solid color"), ("pulse", "Gentle pulse"), ("blink", "Blink"), ("flash", "Flash"), ("effect", "Use port effect")];
    /// <summary>secondary.mjs operatorNames, which the rule tester shares (src/rules.mjs words in and not_in differently).</summary>
    static readonly Dictionary<string, string> OperatorNames = new(StringComparer.Ordinal)
    {
        ["is"] = "is", ["is_not"] = "is not", ["in"] = "is any of", ["not_in"] = "is none of", ["gt"] = "is above", ["ge"] = "is at least",
        ["lt"] = "is below", ["le"] = "is at most", ["eq"] = "equals", ["ne"] = "does not equal", ["between"] = "is between",
    };
    const string SceneRefreshError = "The lighting setup changed. Refresh rules to load it.";
    JsonArray specs = [], templates = [], rules = [];
    JsonObject? sceneBase;
    JsonObject draft = [];
    string? selected, observedSceneKey, latestSceneKey;
    string section = "When";
    bool schedulesTab, edited, reordered, loaded, rulesReady, retryShown, reading;
    readonly Dictionary<string, TextBlock> outputLabels = [];
    long nextOutputUpdate;
    TextBlock? summary, scheduleHint;
    Button? apply;
    ScrollViewer? ruleScroll;
    Vector ruleOffset;
    PortLayout Ports => PortTopology.LightingPorts(Workspace.State);

    public AutomationPage(AppShell shell) : base(shell)
    {
        selected = Studio.RequestedRuleId;
        Studio.RequestedRuleId = null;
        latestSceneKey = SceneKey(Workspace.State);
        Draw();
        Start(ReloadRules);
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

    /// <summary>sceneKey: the revision when it is a safe integer, otherwise the scene fingerprint.</summary>
    static string SceneKey(JsonObject state) =>
        Js.IsSafeInteger(state["revision"]) ? "revision:" + Js.NumberToString(state.Num("revision")!.Value) : SecondaryModel.SceneFingerprint(state);

    public override bool HasChanges => edited || reordered;

    Task<bool> DiscardRule() => !edited ? Task.FromResult(true)
        : Shell.Overlay.Confirm("Discard rule changes?", "Your unapplied rule changes will be discarded.", "Discard");

    Task<bool> DiscardSchedule() => !edited ? Task.FromResult(true)
        : Shell.Overlay.Confirm("Discard schedule changes?", "Your unapplied schedule changes will be discarded.", "Discard");

    /// <summary>The current view's canLeave, used when leaving the page and when switching tabs.</summary>
    Task<bool> LeaveView() => schedulesTab ? DiscardSchedule() : !edited && !reordered ? Task.FromResult(true)
        : Shell.Overlay.Confirm("Discard changes?", "Your unapplied rule and priority changes will be discarded.", "Discard");

    public override async Task<bool> CanLeave() => !Busy && await LeaveView();

    async Task ReloadRules()
    {
        if ((edited || reordered) && !await Shell.Overlay.Confirm("Discard changes?", "Refresh will discard unapplied rule and priority changes.", "Refresh")) return;
        try { await LoadRules(); }
        catch
        {
            if (Active && !schedulesTab)
            {
                rulesReady = false;
                retryShown = true;
                Draw();
            }
            throw;
        }
    }

    async Task LoadRules()
    {
        observedSceneKey = latestSceneKey;
        var fields = Get("/api/rules/fields");
        var examples = Get("/api/rules/templates");
        var scene = Get("/api/scene");
        await Task.WhenAll(fields, examples, scene);
        if (!Active || schedulesTab) return;
        specs = (await fields).Arr("fields");
        templates = (await examples).Arr();
        sceneBase = (await scene).Obj();
        rules = (JsonArray)sceneBase.Arr("scene.rules").DeepClone();
        selected = rules.Objects().FirstOrDefault(r => r.Str("id") == selected)?.Str("id") ?? rules.Objects().FirstOrDefault()?.Str("id");
        ResetRule();
        edited = reordered = false;
        loaded = rulesReady = true;
        retryShown = false;
        Draw();
        Tell("");
    }

    void ResetRule()
    {
        draft = rules.Objects().FirstOrDefault(r => r.Str("id") == selected)?.DeepClone().AsObject() ?? Blank();
        if (draft["ports"] == null) draft["ports"] = Js.NumberArray(Ports.Copper);
        if (draft["conditions"] is not JsonArray) draft["conditions"] = new JsonArray();
        if (draft["look"] is not JsonObject) draft["look"] = new JsonObject();
    }

    async Task Go(bool schedules)
    {
        if (schedules == schedulesTab || !await LeaveView()) return;
        schedulesTab = schedules;
        loaded = edited = reordered = retryShown = false;
        ruleOffset = default;
        Tell("");
        Draw();
        if (schedules) await LoadSchedules();
        else await ReloadRules();
    }

    Control Tabs()
    {
        var tabs = U.Row(6, Tab("Rules", false), Tab("Schedules", true));
        tabs.HorizontalAlignment = HorizontalAlignment.Left;
        tabs.Margin = new Thickness(24, 0, 24, 12);
        AutomationProperties.SetName(tabs, "Automation views");
        return tabs;
    }

    Button Tab(string label, bool schedules)
    {
        var tab = Key(ActionButton(label, () => Go(schedules), "quiet automation-tab"), "tab-" + label);
        tab.Classes.Set("current", schedules == schedulesTab);
        AutomationProperties.SetHelpText(tab, schedules == schedulesTab ? "Selected" : "");
        return tab;
    }

    void Draw()
    {
        if (ruleScroll is { } scroll) ruleOffset = scroll.Offset;
        Body.Children.Clear(); outputLabels.Clear(); summary = null; apply = null; scheduleHint = null; ruleScroll = null;
        if (schedulesTab) DrawSchedules();
        else DrawRules();
        Body.Children.Insert(1, Tabs());
        Body.Children.Add(NoticeBox);
    }

    async Task SelectRule(string id)
    {
        if (!await DiscardRule()) return;
        selected = id; edited = false; retryShown = false;
        ResetRule();
        Draw();
    }

    async Task NewRule()
    {
        if (!await DiscardRule()) return;
        selected = null; edited = false; retryShown = false;
        draft = Blank();
        Draw();
        RequestFocus("rule-name");
    }

    protected override string? FallbackFocus(string key) =>
        key.StartsWith("rule:", StringComparison.Ordinal) ? selected is { } id ? "rule:" + id : "new-rule" : null;

    void DrawRules()
    {
        var newRule = Key(ActionButton("New rule", NewRule, "primary"), "new-rule");
        newRule.IsEnabled = rulesReady;
        Body.Children.Add(Header("Automation", "Choose when your ports change color.", ActionButton("Refresh rules", ReloadRules), newRule));
        var list = U.Column(0);
        AutomationProperties.SetName(list, "Lighting rules");
        var ports = Ports;
        int index = 0;
        foreach (var r in rules.Objects())
        {
            string id = r.Str("id") ?? "";
            var swatch = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) }
                .Bind(Border.BorderBrushProperty, "swatch-highlight");
            if (r.Str("look.color") is { Length: > 0 } color) swatch.Background = U.Brush(color);
            else swatch.Bind(Border.BackgroundProperty, "faint");
            var heading = new DockPanel();
            DockPanel.SetDock(swatch, Dock.Left);
            heading.Children.Add(swatch);
            heading.Children.Add(ItemName($"{++index}. {r.Str("name")}"));
            var output = ItemLine("");
            outputLabels[id] = output;
            var scope = ItemLine($"{(r.Truthy("enabled") ? "Enabled" : "Disabled")} · {SecondaryModel.ScopeSummary(r["ports"], ports.Copper, ports.Sfp)}");
            list.Children.Add(Key(ListItem(heading, [scope, output], id == selected, () => Run(() => SelectRule(id))), "rule:" + id));
        }
        if (rules.Count == 0)
        {
            var empty = U.P("Create a rule to respond to port activity or time.", "muted");
            empty.TextAlignment = TextAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            list.Children.Add(new Border { MinHeight = 150, Padding = new Thickness(30), Child = empty });
        }
        var hint = U.P("Rules run from top to bottom. The first match sets the port lighting.", "faint");
        hint.Margin = new Thickness(9, 12);
        list.Children.Add(hint);
        if (reordered)
        {
            list.Children.Add(HelpText("Priority changes"));
            var actions = U.Wrap(8, ActionButton("Apply priority", SavePriority, "primary"), PlainButton("Cancel priority", () =>
            {
                rules = (JsonArray)(sceneBase?.Arr("scene.rules") ?? []).DeepClone();
                reordered = false;
                Draw();
            }, null));
            actions.Margin = new Thickness(0, 8, 0, 0);
            list.Children.Add(actions);
        }
        Control editor = retryShown ? LoadingPanel("Rules could not be loaded. Your saved rules are unchanged.", ReloadRules)
            : !loaded ? LoadingPanel("Loading rules…") : RuleEditor();
        Body.Children.Add(Split(list, editor, new Thickness(0)));
        UpdateRuleOutput(Workspace.State);
    }

    void Changed() { edited = true; UpdateSummary(); }

    void UpdateSummary()
    {
        var ports = Ports;
        var look = draft.Obj("look");
        int count = draft.Arr("conditions").Count;
        var style = look.Str("style");
        var styleName = RuleStyles.FirstOrDefault(s => s.Value == style).Label ?? style ?? "";
        var level = look.Num("brightness") is { } brightness ? $" at {Js.NumberToString(brightness)}%" : "";
        if (summary != null)
            summary.Text = $"{SecondaryModel.ScopeSummary(draft["ports"], ports.Copper, ports.Sfp)} · {styleName}{level} · {count} {(count == 1 ? "condition" : "conditions")}";
        if (apply != null)
        {
            apply.Content = reordered ? "Apply rule and priority" : "Apply rule";
            apply.IsEnabled = SecondaryModel.ValidateRuleDraft(draft, specs).Length == 0;
        }
    }

    JsonArray Candidate(string id)
    {
        var next = (JsonArray)rules.DeepClone();
        var payload = (JsonObject)draft.DeepClone();
        payload["id"] = id; payload["name"] = draft.Str("name")?.Trim() ?? "";
        var index = next.IndexOf(next.Objects().FirstOrDefault(r => r.Str("id") == id));
        if (index < 0) next.Add(payload); else next[index] = payload;
        return next;
    }

    /// <summary>prepareMutation: resolves a lighting preview first and refuses when the rules changed meanwhile.</summary>
    async Task<bool> PrepareMutation()
    {
        bool hadDraft = Studio.PreviewScene() != null;
        if (!await Studio.ConfirmDraft()) return false;
        if (!hadDraft) return true;
        var latest = (await Get("/api/scene")).Obj();
        if (!Active) return false;
        if (!JsonNode.DeepEquals(latest["revision"], sceneBase?["revision"]))
        {
            if (Js.Stringify(latest.At("scene.rules")) != Js.Stringify(sceneBase.At("scene.rules")))
                throw new InvalidOperationException("The automations changed while the lighting preview was resolved. Your rule edits are kept. Refresh rules to review the updated setup before applying.");
            sceneBase = latest;
            observedSceneKey = latestSceneKey = SceneKey(Workspace.State);
        }
        return true;
    }

    /// <summary>applyRules: commits the rule list; returns the confirmation error for an accepted but unconfirmed write.</summary>
    async Task<string?> ApplyRules(JsonArray next)
    {
        if (sceneBase is null || !sceneBase.Truthy("revision")) throw new InvalidOperationException("Refresh the rules before applying changes.");
        var scene = SecondaryModel.RulesSnapshot(sceneBase["scene"], next);
        JsonObject result;
        string? confirmation = null;
        try { result = (await SceneTransaction.CommitScene(sceneBase, scene, Send)).Obj(); }
        catch (Exception error) when (SceneTransaction.AcceptedScene(error) is JsonObject accepted)
        {
            result = accepted;
            confirmation = "Rule changes are saved, but lighting output was not confirmed. Use Refresh rules to check the current state.";
        }
        sceneBase = result;
        rules = (JsonArray)result.Arr("scene.rules").DeepClone();
        return confirmation;
    }

    async Task RefreshAfterWrite(string message, string? confirmation)
    {
        bool refreshed = true;
        try { await Workspace.Refresh(false); } catch { refreshed = false; }
        if (!Active) return;
        try { Draw(); } catch { refreshed = false; }
        if (confirmation != null) throw new InvalidOperationException(confirmation);
        Tell(refreshed ? message : $"{message} Status could not be refreshed. Use Refresh rules to check it.", !refreshed);
    }

    async Task SaveRule()
    {
        var (error, place) = RuleError();
        if (error.Length != 0)
        {
            if (place != null && place != section && sectionNavShown) { section = place; Draw(); }
            Tell(error, true);
            return;
        }
        var id = selected ?? Guid.NewGuid().ToString("N");
        if (!await PrepareMutation()) return;
        var confirmation = await ApplyRules(Candidate(id));
        selected = id;
        ResetRule();
        edited = reordered = false;
        await RefreshAfterWrite("Rule applied.", confirmation);
    }

    async Task SavePriority()
    {
        if (!await PrepareMutation()) return;
        var confirmation = await ApplyRules(rules);
        reordered = false;
        await RefreshAfterWrite("Rule priority applied.", confirmation);
    }

    async Task DeleteRule()
    {
        var id = selected;
        if (!await PrepareMutation()) return;
        if (!await Shell.Overlay.Confirm("Delete rule?", "This rule will be removed from your lighting setup.", "Delete")) return;
        var pendingOrder = reordered ? rules.Objects().Where(r => r.Str("id") != id).Select(r => r.Str("id")).ToList() : null;
        var remaining = new JsonArray(sceneBase.Objects("scene.rules").Where(r => r.Str("id") != id).Select(r => r.Copy()).ToArray());
        var confirmation = await ApplyRules(remaining);
        if (pendingOrder != null)
        {
            var saved = rules.Objects().Select(r => r.Str("id")).ToList();
            rules = new JsonArray(rules.Objects().OrderBy(r => pendingOrder.IndexOf(r.Str("id"))).Select(r => r.Copy()).ToArray());
            reordered = !rules.Objects().Select(r => r.Str("id")).SequenceEqual(saved);
        }
        selected = rules.Objects().FirstOrDefault()?.Str("id");
        ResetRule();
        edited = false;
        await RefreshAfterWrite("Rule deleted.", confirmation);
    }

    /// <summary>updateRuleOutput: the schedule hint and each rule's output line.</summary>
    void UpdateRuleOutput(JsonObject st)
    {
        ScheduleClock.Receive(st["schedule_clock"]);
        if (scheduleHint != null)
        {
            var zone = ScheduleClock.Label(st);
            var text = $"Schedule time: {(zone == "EE computer" ? zone : $"{zone} · EE computer")}. A time range can continue past midnight.";
            if (scheduleHint.Text != text) scheduleHint.Text = text;
        }
        if (outputLabels.Count == 0) return;
        var counts = new Dictionary<string, int>();
        bool available = st.Objects("ports").Any(p => p["source"] != null) || st.Objects("sfp").Any(p => p.ContainsKey("rule"));
        if (st.Str("mode") == "running")
            foreach (var id in st.Objects("ports").Where(p => p.Str("source.kind") == "rule").Select(p => p.Str("source.id"))
                         .Concat(st.Objects("sfp").Select(p => p.Str("rule.id"))).OfType<string>().Where(id => id.Length > 0))
                counts[id] = counts.GetValueOrDefault(id) + 1;
        foreach (var (id, label) in outputLabels)
        {
            int n = counts.GetValueOrDefault(id);
            var text = st.Str("mode") != "running" ? "EE output is stopped" : available ? $"Controlling {n} {(n == 1 ? "port" : "ports")}" : "Waiting for output status";
            if (label.Text != text) label.Text = text;
        }
    }

    /// <summary>
    /// refreshCleanScene: while the editor has no unapplied changes, a changed scene is read again in the background,
    /// and the page redraws only when the rules themselves changed.
    /// </summary>
    void RefreshCleanScene()
    {
        if (!Active || schedulesTab || sceneBase is null || edited || reordered || Busy || reading || latestSceneKey == observedSceneKey) return;
        var key = latestSceneKey;
        var baseScene = sceneBase;
        observedSceneKey = key;
        reading = true;
        _ = ReadScene(key, baseScene);
    }

    async Task ReadScene(string? key, JsonObject baseScene)
    {
        try
        {
            var result = (await Get("/api/scene")).Obj();
            if (!Active || schedulesTab || baseScene != sceneBase || key != latestSceneKey) return;
            if (edited || reordered || Busy) { observedSceneKey = null; return; }
            var previousRules = Js.Stringify(sceneBase.At("scene.rules"));
            sceneBase = result;
            if (NoticeIs(SceneRefreshError)) Tell("");
            if (Js.Stringify(result.At("scene.rules")) == previousRules) return;
            rules = (JsonArray)result.Arr("scene.rules").DeepClone();
            if (selected != null && !rules.Objects().Any(r => r.Str("id") == selected)) selected = rules.Objects().FirstOrDefault()?.Str("id");
            ResetRule();
            Act(Draw);
        }
        catch
        {
            if (Active && !schedulesTab && baseScene == sceneBase && key == latestSceneKey && !edited && !reordered && !Busy) Tell(SceneRefreshError, true);
        }
        finally
        {
            reading = false;
            RefreshCleanScene();
        }
    }

    public override void Frame()
    {
        long now = Environment.TickCount64;
        if (Active && loaded && !schedulesTab && now >= nextOutputUpdate)
        {
            nextOutputUpdate = now + 500;
            UpdateRuleOutput(Workspace.State);
        }
    }

    public override void Update()
    {
        if (!Active || schedulesTab) return;
        UpdateRuleOutput(Workspace.State);
        latestSceneKey = SceneKey(Workspace.State);
        RefreshCleanScene();
    }
}
