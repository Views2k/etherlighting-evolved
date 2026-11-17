using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;
using U = EtherlightingEvolved.Native.Ui.Ui;
using LS = EtherlightingEvolved.Native.Core.Model.LightingState;
using PM = EtherlightingEvolved.Native.Core.Model.PortMap;

namespace EtherlightingEvolved.Native.Pages.Lighting;

public sealed class LightingStudio(AppShell shell) : IStudio
{
    readonly Dictionary<string, JsonObject> remembered = new(StringComparer.Ordinal);
    JsonObject? baseline, pendingBefore, pendingScene, undoBefore, undoAfter;
    string? pendingWorkspace, undoWorkspace, undoMode, pendingMode;
    int confirmed;
    Task<bool>? confirming;
    internal event Action<bool>? Changed;
    Workspace W => shell.Workspace;
    internal JsonObject State => W.State;
    internal string Editor { get; set; } = "effect";
    internal bool Compare { get; set; }
    internal (string Type, JsonObject Settings)? Appearance { get; set; }
    internal bool Multiple { get; set; }
    internal int? Anchor { get; set; }
    public IReadOnlyCollection<int> Selected { get; private set; } = Array.Empty<int>();
    public IReadOnlyDictionary<string, JsonObject> RememberedEffects => remembered;
    public JsonObject Draft { get; private set; } = new();
    public JsonObject GalleryState { get; private set; } = new();
    public string? RequestedRuleId { get; set; }
    public bool HasEdits => Draft.Count > 0;
    public bool HasPendingPlan => pendingBefore is not null;
    public bool CanUndoApply => !HasEdits && undoAfter is not null;
    internal List<int> Copper => PortTopology.LightingPorts(State).Copper.ToList();
    internal List<int> Optical => PortTopology.LightingPorts(State).Sfp.ToList();
    internal List<int> All => [.. Copper, .. Optical];
    internal List<int> SelectedCopper => Copper.Where(Selected.Contains).ToList();
    internal List<int> SelectedOptical => Optical.Where(Selected.Contains).ToList();
    internal List<int> GroupedOptical => SelectedOptical.Where(i => EffectDraft.PortEffect(State, Draft, i) is not null).ToList();
    internal string Scope(IEnumerable<int>? ports = null)
    {
        var ids = (ports ?? Selected).Distinct().Order().ToList();
        if (ids.Count == 0)
            return "No ports selected";
        if (ids.SequenceEqual(All.Order()))
            return "All ports";
        if (ids.SequenceEqual(Copper.Order()))
            return "All Ethernet ports";
        if (ids.SequenceEqual(Optical.Order()))
            return "All SFP ports";
        if (ids.Count == 1)
            return $"{(Optical.Contains(ids[0]) ? "SFP" : "Port")} {ids[0]}";
        return $"Ports {PortTopology.PortRanges(ids)}";
    }
    internal void Select(IEnumerable<int> ports)
    {
        Selected = ports.Intersect(All).Order().ToArray();
        var following = PortTopology.PortIds(Draft["returnToEffect"]);
        Editor = Selected.Count > 0 && Selected.All(id => Draft.Obj("colors").ContainsKey(id.ToString())
            || !following.Contains(id) && EffectDraft.EffectDraftForPort(Draft, id).Bool("clearOverrides") != true
                && LS.ControlSource(State, id, W.Effects).Str("kind") == "color") ? "color" : "effect";
        Signal(true);
    }
    internal async Task<bool> SelectFromMap(IEnumerable<int> ports)
    {
        var next = ports.Distinct().ToArray();
        if (next.Except(All).Any())
            throw new InvalidOperationException("Choose ports on the connected switch.");
        if (HasPendingPlan && !await ConfirmDraft())
            return false;
        if (shell.CurrentPage is LightingPage page && !page.ValidateEditor())
        {
            shell.Notify("Correct the highlighted value before selecting other ports.", true);
            return false;
        }
        Select(next);
        return true;
    }
    internal void Signal(bool editor = true)
    {
        SaveUiState(shell.PageName);
        shell.UpdateDraftBar();
        Changed?.Invoke(editor);
    }
    /// <summary>
    /// Changes the preview. A change the current setup does not accept (for example, the lighting changed on
    /// another device while Port controls was open) is shown as an error and leaves the preview as it was.
    /// Returns false when nothing changed.
    /// </summary>
    internal bool Edit(Func<JsonObject, JsonObject> transform, bool rebuild = false)
    {
        if (HasPendingPlan || W.Busy > 0)
            return false;
        var before = baseline ?? SceneReview.SceneFromState(State);
        JsonObject next;
        try
        {
            next = transform((JsonObject)Draft.DeepClone());
            LS.LightingOperations(State, next);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or KeyNotFoundException or NullReferenceException)
        {
            shell.Notify(error is InvalidOperationException or ArgumentException && error.Message.Length > 0
                ? error.Message : "This setting no longer matches the current lighting. Review the current setup.", true);
            Changed?.Invoke(true);
            return false;
        }
        baseline = next.Count == 0 ? null : before;
        Draft = next;
        Compare = false;
        Signal(rebuild);
        return true;
    }
    internal bool Set(string field, JsonNode? value, bool rebuild = false) =>
        Edit(d =>
        {
            d[field] = value.Copy();
            return d;
        }, rebuild);
    internal bool StageEffect(JsonObject config, bool rebuild = true) =>
        Edit(d => EffectDraft.StageEffectDraft(d, config, "group-" + Guid.NewGuid(), Copper), rebuild);
    /// <summary>
    /// Previews saved effect settings on the selected ports. Fixed colors on those ports are kept, as in the
    /// browser workspace; choosing an effect in the gallery is what replaces them.
    /// </summary>
    internal bool PreviewSavedEffect(JsonObject settings)
    {
        if (Selected.Count == 0)
            throw new InvalidOperationException("Select ports for these settings.");
        var chosen = (JsonObject)settings.DeepClone();
        chosen["ports"] = Js.NumberArray(Selected);
        chosen["clearOverrides"] = false;
        Editor = "effect";
        return StageEffect(chosen);
    }
    public async Task ChooseEffect(JsonObject config)
    {
        var ports = config["ports"] is JsonArray a ? PortTopology.PortIds(a) : Selected.ToList();
        if (ports.Count == 0)
        {
            shell.Notify("Select ports to apply an effect.");
            return;
        }
        Selected = ports.Intersect(All).ToArray();
        if (Selected.Count == 0)
            return;
        var chosen = (JsonObject)config.DeepClone();
        chosen["ports"] = Js.NumberArray(Selected);
        chosen["clearOverrides"] = true;
        Editor = "effect";
        StageEffect(chosen);
        await shell.Navigate("Lighting", true);
        if (shell.CurrentPage is LightingPage page)
            page.RevealEditor();
    }
    public async Task ChooseSfpEffect(JsonObject effect, IReadOnlyList<int> ports)
    {
        Selected = ports.Intersect(Optical).ToArray();
        if (Selected.Count == 0)
        {
            shell.Notify("Select SFP ports to apply an effect.");
            return;
        }
        var id = effect.Str("effect") ?? effect.Str("id") ?? "steady";
        Edit(d =>
        {
            d = EffectDraft.LeaveSfpEffect(d, GroupedOptical);
            var sync = SfpSyncModel.SfpSync(State, d);
            if (sync.Str("mode") == "ethernet")
            {
                sync["mode"] = "together";
                d["sfp_sync"] = sync;
            }
            var lights = Ensure(d, "sfp");
            foreach (var p in SfpSyncModel.SfpEditTargets(State, Selected, d))
                lights[p.ToString()] = SfpSyncModel.NormalizeSfpBrightness(Js.Spread(LS.SfpSettings(SfpSyncModel.SfpCandidateSettings(State, p, id, d)), new JsonObject { ["mode"] = "color" }));
            return d;
        }, true);
        await shell.Navigate("Lighting", true);
        if (shell.CurrentPage is LightingPage page)
            page.RevealEditor();
    }
    internal static JsonObject Ensure(JsonObject obj, string key)
    {
        if (obj[key] is not JsonObject child)
        {
            child = new();
            obj[key] = child;
        }
        return child;
    }
    public Task ChangeSelection() => shell.Navigate("Lighting", true);
    public string DraftSummary()
    {
        if (HasPendingPlan)
            return $"{confirmed} confirmed · {(pendingScene is null ? 0 : 1) + (pendingMode is null ? 0 : 1)} remaining";
        var parts = new List<string>();
        var targets = new HashSet<int>();
        foreach (var effect in EffectDraft.EffectDrafts(Draft))
        {
            var name = W.Effects.Objects().FirstOrDefault(e => e.Str("id") == effect.Str("id")).Str("name") ?? "Effect";
            if (!parts.Contains(name))
                parts.Add(name);
            targets.UnionWith(PortTopology.PortIds(effect.At("ports")));
        }
        if (Draft.Obj("colors").Count > 0)
        {
            parts.Add("Fixed colors");
            foreach (var k in Draft.Obj("colors"))
                if (int.TryParse(k.Key, out var p))
                    targets.Add(p);
        }
        if (Draft.Arr("returnToEffect").Count > 0)
        {
            parts.Add("Follow group effect");
            targets.UnionWith(PortTopology.PortIds(Draft["returnToEffect"]));
        }
        if (SfpSyncModel.SfpDraftPorts(State, Draft) is { Count: > 0 } sfp)
        {
            parts.Add("SFP lighting");
            targets.UnionWith(sfp);
        }
        if (Draft["sfp_sync"] is not null)
            parts.Add("SFP synchronization");
        if (Draft["brightness"] is not null)
            parts.Add("Default brightness");
        if (Draft.Str("mode") is { } mode)
            parts.Add(mode == "running" ? "Enable EE lighting" : "Return to UniFi");
        return $"{string.Join(", ", parts)} · {(targets.Count > 0 ? Scope(targets) : "Entire setup")} · Unsaved preview";
    }
    void ClearDraft()
    {
        Draft = new();
        baseline = pendingBefore = pendingScene = null;
        pendingMode = pendingWorkspace = null;
        confirmed = 0;
        Compare = false;
    }
    internal void Discard()
    {
        if (W.Busy > 0)
            return; // A change is applying; its result decides what is left to discard.
        var partial = HasPendingPlan;
        ClearDraft();
        Signal();
        if (partial)
            shell.Notify("Preview discarded. Saved changes remain applied.");
    }
    public Task<bool> ConfirmDraft() => !HasEdits && W.Busy == 0 ? Task.FromResult(true) : confirming ??= ConfirmAndClear();
    async Task<bool> ConfirmAndClear()
    {
        await Task.Yield();
        try
        {
            await W.Idle();
            if (!HasEdits)
                return true;
            var content = U.Column(12, U.H2("Unapplied lighting changes"), U.P("Your lighting edits are being previewed."));
            var dialog = shell.Overlay.Show(content, 445);
            var error = U.P("", "error");
            content.Children.Add(error);
            var apply = U.Button("Apply changes");
            var cancel = U.Button("Keep editing", "primary", () => dialog.Close("stay"));
            var discard = U.Button("Discard", "quiet", () => dialog.Close("discard"));
            content.Children.Add(U.Wrap(8, cancel, discard, apply));
            apply.Click += async (_, _) =>
            {
                apply.IsEnabled = discard.IsEnabled = cancel.IsEnabled = false;
                dialog.Canceling = () => false;
                try
                {
                    await Apply();
                    if (!HasEdits)
                        dialog.Close("applied");
                }
                catch (Exception ex) { error.Text = ex.Message; }
                finally
                {
                    dialog.Canceling = null;
                    apply.IsEnabled = discard.IsEnabled = cancel.IsEnabled = true;
                }
            };
            var result = await dialog.Result;
            if (result == "discard")
                Discard();
            return result is "discard" or "applied";
        }
        finally { confirming = null; }
    }
    async Task<JsonObject?> Review(JsonObject before, JsonObject desired)
    {
        var review = SceneReview.ReviewContent(baseline, before, desired);
        var content = U.Column(12, U.H2(review.Heading), U.P(review.Message), U.H3(review.RemoteHeading));
        foreach (var item in review.RemoteChanges)
            content.Children.Add(U.P(item));
        content.Children.Add(U.H3("Your preview"));
        foreach (var item in review.Changes)
            content.Children.Add(U.P(item));
        var dialog = shell.Overlay.Show(content, 540);
        var buttons = U.Wrap();
        for (var i = 0; i < review.Actions.Count; i++)
        {
            var j = i;
            buttons.Children.Add(U.Button(review.Actions[j].Label, j == review.Actions.Count - 1 ? "primary" : "quiet", () => dialog.Close(j.ToString())));
        }
        content.Children.Add(buttons);
        return int.TryParse(await dialog.Result, out var chosen) ? review.Actions[chosen].Scene : null;
    }
    internal async Task Apply()
    {
        if (!HasEdits)
            return;
        if (shell.CurrentPage is LightingPage page && !page.ValidateEditor())
            throw new InvalidOperationException("Correct the highlighted value before applying.");
        await W.Serialize(async () =>
        {
            if (W.Disconnected || !W.Connected)
                throw new InvalidOperationException("Reconnect the switch before applying. Your preview is preserved.");
            var send = W.WorkspaceRequest();
            var workspace = W.WorkspaceId;
            if (!HasPendingPlan)
            {
                LS.LightingOperations(State, Draft);
                var before = (await send("/api/scene", "GET", null)).Obj();
                var scene = LS.LightingSnapshot(before["scene"], State, Draft);
                if (baseline is not null && Fingerprint(baseline) != Fingerprint(before["scene"]))
                {
                    var reviewed = await Review(before.Obj("scene"), LS.LightingSnapshot(baseline, State, Draft));
                    if (reviewed is null)
                        return;
                    scene = reviewed;
                }
                if (workspace != W.WorkspaceId)
                    throw new InvalidOperationException("The connected switch changed.");
                pendingBefore = before;
                pendingScene = Fingerprint(before["scene"]) == Fingerprint(scene) ? null : scene;
                pendingMode = Draft.Str("mode") is { } mode && mode != State.Str("mode") ? mode : null;
                pendingWorkspace = workspace;
                confirmed = 0;
                undoBefore = before;
                undoMode = State.Str("mode");
                undoWorkspace = workspace;
                undoAfter = null;
            }
            if (workspace != pendingWorkspace)
                throw new InvalidOperationException("The connected switch changed.");
            try
            {
                if (pendingScene is not null)
                {
                    try
                    {
                        undoAfter = (await SceneTransaction.CommitScene(pendingBefore, pendingScene, send)).Obj();
                    }
                    catch (Exception ex)
                    {
                        if (SceneTransaction.AcceptedScene(ex) is JsonObject accepted)
                        {
                            undoAfter = accepted;
                            pendingScene = null;
                            confirmed++;
                        }
                        throw;
                    }
                    pendingScene = null;
                    confirmed++;
                    Signal(false);
                }
                if (pendingMode is not null)
                {
                    await send(pendingMode == "running" ? "/api/resume" : "/api/release", "POST", null);
                    pendingMode = null;
                    confirmed++;
                    undoBefore = undoAfter = null;
                }
                if (workspace != W.WorkspaceId)
                    return;
            }
            catch
            {
                if (workspace == W.WorkspaceId)
                {
                    if (pendingScene is null && pendingMode is null)
                        ClearDraft();
                    Signal();
                    try
                    {
                        await W.Refresh(false);
                    }
                    catch { }
                }
                throw;
            }
            foreach (var e in EffectDraft.EffectDrafts(Draft).OfType<JsonObject>())
                if (e.Str("id") is { } id)
                    remembered[id] = (JsonObject)e.DeepClone();
            var changed = Draft.ContainsKey("brightness") ? All : EffectDraft.EffectDrafts(Draft).SelectMany(e => PortTopology.PortIds(e.At("ports")))
                .Concat(Draft.Obj("colors").Select(x => int.Parse(x.Key)))
                .Concat(PortTopology.PortIds(Draft["returnToEffect"]))
                .Concat(SfpSyncModel.SfpDraftPorts(State, Draft))
                .Distinct().ToList();
            var savedScene = undoAfter?["scene"] ?? pendingBefore?["scene"];
            ClearDraft();
            Signal();
            try
            {
                await W.Refresh(false);
                var matches = LS.SceneAutomationMatches(savedScene, State, changed);
                shell.Notify(matches.Count == 0 ? "Changes applied" : $"Setup saved. Automation matches ports {PortTopology.PortRanges(matches.Objects().Select(x => x.Int("port") ?? 0))}.");
            }
            catch { shell.Notify("Changes were applied. Reconnect to refresh the status.", true); }
        });
        Signal();
    }
    static string Fingerprint(JsonNode? s) => SecondaryModel.SceneFingerprint(s);
    public JsonObject? PreviewScene() => HasEdits && !HasPendingPlan ? LS.LightingSnapshot(SceneReview.SceneFromState(State), State, Draft) : null;
    public async Task SavePreviewProfile(string name)
    {
        if (!HasEdits || HasPendingPlan || Draft.ContainsKey("mode"))
            throw new InvalidOperationException("Apply or discard the lighting control change before saving this preview.");
        await W.Serialize(async () =>
        {
            var send = W.WorkspaceRequest();
            var before = (await send("/api/scene", "GET", null)).Obj();
            if (baseline is not null && Fingerprint(baseline) != Fingerprint(before["scene"]))
                throw new InvalidOperationException("The lighting setup changed. Review the current setup before saving this preview.");
            await send("/api/presets", "POST", new JsonObject
            {
                ["name"] = name, ["scene"] = LS.LightingSnapshot(before["scene"], State, Draft), ["expected_revision"] = before["revision"].Copy(), ["overwrite"] = false,
            });
        });
    }
    public async Task ApplyProfile(JsonNode reference)
    {
        if (!await ConfirmDraft())
            return;
        await W.Serialize(async () =>
        {
            var send = W.WorkspaceRequest();
            var scope = W.WorkspaceId;
            var records = (await send("/api/profiles", "GET", null)).Objects("profiles");
            var chosen = records.FirstOrDefault(p => reference.Str() is { } name ? p.Str("name") == name : p.Str("id") == reference.Str("id"));
            if (chosen is null || reference.At("revision") is { } revision && !JsonNode.DeepEquals(chosen["revision"], revision))
                throw new InvalidOperationException("This profile changed. Refresh profiles and choose it again.");
            var notice = ProfilePreview.ProfileLayoutNotice(chosen["scene"], State);
            if (notice.Length > 0)
                throw new InvalidOperationException(notice);
            var before = (await send("/api/scene", "GET", null)).Obj();
            JsonObject? after;
            try
            {
                after = (await SceneTransaction.CommitScene(before, chosen["scene"], send, false)).Obj();
            }
            catch (Exception ex)
            {
                if (scope == W.WorkspaceId && SceneTransaction.AcceptedScene(ex) is JsonObject accepted)
                {
                    undoBefore = before;
                    undoAfter = accepted;
                    undoMode = State.Str("mode");
                    undoWorkspace = scope;
                }
                throw;
            }
            if (scope != W.WorkspaceId)
                return;
            undoBefore = before;
            undoAfter = after;
            undoMode = State.Str("mode");
            undoWorkspace = scope;
            await W.Refresh(false);
            Signal();
            shell.Notify("Profile applied");
        });
    }
    public async Task UndoLastApply()
    {
        if (W.Busy > 0 || !CanUndoApply || !await ConfirmDraft())
            return;
        await W.Serialize(async () =>
        {
            if (undoAfter is null || undoBefore is null)
                return;
            if (undoMode != State.Str("mode") || W.WorkspaceId != undoWorkspace)
                throw new InvalidOperationException("Lighting control changed. Review the current setup before undoing.");
            await W.WorkspaceRequest()("/api/scene", "PUT",
                new JsonObject { ["scene"] = undoBefore["scene"].Copy(), ["expected_revision"] = undoAfter["revision"].Copy(), ["confirm_output"] = true });
            undoBefore = undoAfter = null;
            await W.Refresh(false);
            Signal();
            shell.Notify("Previous lighting restored");
        });
    }
    string? StorageKey => W.Session.Bool("logged_in") == true && W.WorkspaceId is { Length: 64 } id && id.All(Uri.IsHexDigit) ? "ee.workspace.v2." + id : null;
    public void SaveUiState(string page)
    {
        if (!W.Ready || StorageKey is not { } key)
            return;
        var configs = new JsonObject();
        foreach (var (id, cfg) in remembered)
            configs[id] = cfg.DeepClone();
        var state = new JsonObject
        {
            ["page"] = page, ["editor"] = Editor, ["selected"] = Js.NumberArray(Selected), ["galleryState"] = GalleryState.DeepClone(), ["effects"] = configs,
            ["draft"] = Draft.DeepClone(), ["draftBaseScene"] = baseline.Copy(), ["recoveryBefore"] = pendingBefore.Copy(), ["recoveryMode"] = undoMode,
        };
        Preferences.SetSession(key, state.ToJsonString());
    }
    public async Task<string?> Restore()
    {
        Selected = Copper;
        var key = StorageKey;
        if (key is null || Preferences.GetSession(key) is not { } data || Json.Parse(data) is not JsonObject saved)
            return null;
        Selected = PortTopology.PortIds(saved["selected"]).Intersect(All).ToArray();
        Editor = saved.Str("editor") == "color" ? "color" : "effect";
        GalleryState = (JsonObject)saved.Obj("galleryState").DeepClone();
        remembered.Clear();
        foreach (var (id, cfg) in saved.Obj("effects"))
            if (cfg is JsonObject item && W.Effects.Objects().Any(e => e.Str("id") == id))
                remembered[id] = (JsonObject)item.DeepClone();
        if (saved["draft"] is JsonObject { Count: > 0 } restored)
        {
            try
            {
                LS.LightingOperations(State, restored);
                Draft = (JsonObject)restored.DeepClone();
                baseline = saved["draftBaseScene"] as JsonObject ?? SceneReview.SceneFromState(State);
            }
            catch { shell.Notify("The saved preview has invalid settings.", true); }
            if (HasEdits && saved["recoveryBefore"] is JsonObject before)
            {
                try
                {
                    var current = (await W.WorkspaceRequest()("/api/scene", "GET", null)).Obj();
                    if (Fingerprint(current["scene"]) == Fingerprint(LS.LightingSnapshot(before["scene"], State, Draft)))
                    {
                        undoBefore = before;
                        undoAfter = current;
                        undoWorkspace = W.WorkspaceId;
                        undoMode = saved.Str("recoveryMode");
                        var mode = Draft.Str("mode");
                        ClearDraft();
                        if (mode is not null && mode != State.Str("mode"))
                        {
                            Draft["mode"] = mode;
                            baseline = current.Obj("scene");
                        }
                    }
                }
                catch {/* A reconnect can retry the saved preview. */}
            }
        }
        return HasEdits ? "Lighting" : saved.Str("page");
    }
    public void Reset()
    {
        ClearDraft();
        Selected = [];
        remembered.Clear();
        GalleryState = new();
        RequestedRuleId = null;
        undoBefore = undoAfter = null;
        Appearance = null;
        Anchor = null;
        Multiple = false;
        Editor = "effect";
    }
    public void Refreshed(bool rebuild)
    {
        if (undoAfter is not null && !HasPendingPlan && Fingerprint(SceneReview.SceneFromState(State)) != Fingerprint(undoAfter["scene"]))
            undoBefore = undoAfter = null;
        if (rebuild)
            Changed?.Invoke(!HasEdits);
    }
}
