// Port of preview/draft-preview.mjs: the port map colors an unapplied lighting draft would show. Only ports the draft
// changes get a color; the caller shows the live color for the others.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>The motion and color clocks of one previewed group (the previewClocks entries).</summary>
public sealed class EffectClockPair
{
    public EffectClock Motion { get; } = new();

    public EffectClock Color { get; } = new();
}

public static class DraftPreview
{
    static double Number(JsonNode? value, double fallback = 0)
    {
        var number = Js.ToNumber(value);
        return double.IsFinite(number) ? number : fallback;
    }

    static double? Gamma(JsonNode? state) => Js.Num(Js.Get(Js.Get(state, "output"), "gamma"));

    static JsonObject? Find(JsonNode? list, double port) => Js.Objects(list).FirstOrDefault(item => Js.StrictEquals(item["port"], port));

    static bool PortKey(JsonNode? port, out int key)
    {
        key = 0;
        if (Js.Num(port) is not { } n || n != Math.Floor(n) || Math.Abs(n) > int.MaxValue) return false;
        key = (int)n;
        return true;
    }

    /// <summary>
    /// previewDraftColors(state, draft, time, {previewClocks, previewStates}): RGB colors by port for every port the
    /// draft changes. Pass the same clocks and states on each frame so animations continue smoothly.
    /// </summary>
    public static Dictionary<int, double[]> PreviewDraftColors(JsonNode? state, JsonNode? draft, double time,
        Dictionary<string, EffectClockPair>? previewClocks = null, Dictionary<int, EffectAnimationState>? previewStates = null)
    {
        previewClocks ??= [];
        previewStates ??= [];
        var result = new Dictionary<int, double[]>();
        var brightnessPresent = Js.Has(draft, "brightness");
        var master = Number(Js.Get(draft, "brightness") ?? Js.Get(state, "brightness"), 100) / 100;
        var date = ScheduleClock.HostDate(state);
        var hour = date is { } now ? now.Hour + now.Minute / 60.0 + now.Second / 3600.0 : double.NaN;
        var rules = Js.Get(state, "rules");
        var rulesOrEmpty = Js.Truthy(rules) ? rules : new JsonArray();
        var gamma = Gamma(state);
        var stateBrightness = Number(Js.Get(state, "brightness"), 100);
        var statePorts = Js.Get(state, "ports");
        var stateSfp = Js.Get(state, "sfp");
        if (brightnessPresent)
        {
            foreach (var port in Js.Items(Js.Truthy(statePorts) ? statePorts : null).Concat(Js.Items(Js.Truthy(stateSfp) ? stateSfp : null)))
                if (PortKey(Js.Get(port, "port"), out var key)) result[key] = PortDisplay.MasterDisplayRgb(port, master * 100, stateBrightness, rules, gamma, date);
        }
        void Render(JsonNode? config, List<double> ports, HashSet<double> targets)
        {
            var normalized = Js.Spread(config);
            normalized["params"] = Js.Spread(EffectRenderer.DefaultConfig(Js.Get(config, "id"))["params"], Js.Get(config, "params"));
            var key = Js.ToStr(Js.Get(config, "id")) + ":" + string.Join(",", ports.Select(Js.NumberToString));
            if (!previewClocks.TryGetValue(key, out var clocks)) previewClocks[key] = clocks = new EffectClockPair();
            var phase = EffectRenderer.PreviewPhase(clocks.Motion, normalized, time);
            var colorPhase = EffectRenderer.PreviewColorPhase(clocks.Color, normalized, time);
            var resolvedParams = EffectRenderer.ResolvedColorParams(normalized, time, colorPhase);
            var telemetry = ports.Select(id => Find(statePorts, id) ?? Find(stateSfp, id)).ToList();
            var peakTraffic = EffectRenderer.ActivityPeak(telemetry.Select(port => Js.Truthy(port?["facts"]) ? port!["facts"] : new JsonObject()),
                Js.Truthy(normalized["params"]!["include_uplinks"]));
            for (var index = 0; index < ports.Count; index++)
            {
                var port = ports[index];
                if (!targets.Contains(port)) continue;
                if (port != Math.Floor(port)) continue;
                var id = (int)port;
                if (Js.Truthy(Js.Get(Js.Get(state, "overrides"), Js.Key(port))) && !Js.Includes(Js.Get(draft, "returnToEffect"), port)
                    && !Js.Truthy(Js.Get(EffectDraft.EffectDraftForPort(draft, port), "clearOverrides")))
                {
                    var current = Find(statePorts, port) ?? Find(stateSfp, port);
                    result[id] = PortDisplay.MasterDisplayRgb(current, master * 100, stateBrightness, rules, gamma, date);
                    continue;
                }
                JsonNode live = (JsonNode?)telemetry[index] ?? new JsonObject();
                var optical = Find(stateSfp, port);
                if (Js.StrictEquals(normalized["id"], "daylight") && !double.IsFinite(hour))
                {
                    if (!result.ContainsKey(id)) result[id] = PortDisplay.PortDisplayRgb(live, gamma);
                    continue;
                }
                var facts = Js.Truthy(Js.Get(live, "facts")) ? Js.Get(live, "facts") : new JsonObject();
                var rule = optical is not null ? Rules.MatchingSfpRule(rulesOrEmpty, optical, JsonValue.Create("color"), date)
                    : Rules.MatchingRule(rulesOrEmpty, port, facts, date);
                if (!previewStates.TryGetValue(id, out var animation)) previewStates[id] = animation = new EffectAnimationState();
                var color = EffectRenderer.EffectColor(normalized, index, ports.Count, time, new EffectFacts(facts)
                {
                    Hour = hour, Phase = phase, ColorPhase = colorPhase, ResolvedParams = resolvedParams, PeakTraffic = peakTraffic, AnimationState = animation,
                });
                var look = Js.Get(rule, "look");
                var level = (Js.Get(look, "brightness") is { } ruleLevel ? Js.ToNumber(ruleLevel) : master * 100) / 100;
                double opticalLevel = 1;
                if (optical is not null)
                {
                    var opticalBrightness = Js.Get(Js.Get(Js.Get(draft, "sfp"), Js.Key(port)), "brightness") ?? optical["brightness"];
                    opticalLevel = (opticalBrightness is null ? 100 : Js.ToNumber(opticalBrightness)) / 100;
                }
                result[id] = EffectRenderer.Dim(rule is not null ? Rules.RenderLook(look, color, time) : color, level * opticalLevel);
            }
        }
        var baseScene = new JsonObject
        {
            ["zones"] = Js.Truthy(Js.Get(state, "zones")) ? Js.Clone(Js.Get(state, "zones")) : new JsonArray(),
            ["rules"] = Js.Truthy(rules) ? Js.Clone(rules) : new JsonArray(),
            ["overrides"] = Js.Truthy(Js.Get(state, "overrides")) ? Js.Clone(Js.Get(state, "overrides")) : new JsonObject(),
        };
        if (Js.Has(state, "brightness")) baseScene["brightness"] = Js.Clone(Js.Get(state, "brightness"));
        var sfpSettings = new JsonObject();
        foreach (var light in Js.Items(Js.Truthy(stateSfp) ? stateSfp : null)) sfpSettings[Js.ToStr(Js.Get(light, "port"))] = LightingState.SfpSettings(light);
        baseScene["sfp"] = sfpSettings;
        if (Js.Has(state, "sfp_sync")) baseScene["sfp_sync"] = Js.Clone(Js.Get(state, "sfp_sync"));
        var projected = LightingState.Snapshot(baseScene, state, draft, false);
        var returned = Js.Items(Js.Truthy(Js.Get(draft, "returnToEffect")) ? Js.Get(draft, "returnToEffect") : null).ToList();
        List<double> Ports(JsonNode? zone) => Js.Items(Js.Get(zone, "ports")).Select(Js.ToNumber).ToList();
        foreach (var zone in Js.Objects(projected["zones"]))
        {
            var prior = Js.Objects(Js.Get(state, "zones")).FirstOrDefault(item => Js.StrictEquals(item["id"], zone["id"]));
            var changed = prior is null || Js.Stringify(prior["ports"]) != Js.Stringify(zone["ports"]) || Js.Stringify(prior["effect"]) != Js.Stringify(zone["effect"]);
            var ports = Ports(zone);
            var targets = changed ? ports.ToHashSet()
                : ports.Where(port => returned.Any(item => Js.StrictEquals(item, port)) || EffectDraft.EffectDraftForPort(draft, port) is not null).ToHashSet();
            if (targets.Count > 0) Render(zone["effect"], ports, targets);
        }
        foreach (var (port, color) in Js.Entries(Js.Truthy(Js.Get(draft, "colors")) ? Js.Get(draft, "colors") : null))
            if (PortKey(JsonValue.Create(Js.StringToNumber(port)), out var key))
                result[key] = PortDisplay.ManualDisplayRgb(Js.Get(color, "color"), Js.Get(color, "brightness"), master * 100);
        var sync = SfpSyncModel.SfpSync(state, draft);
        var sfpPorts = SfpSyncModel.SfpDraftPorts(state, draft)
            .Concat(brightnessPresent ? Js.Objects(Js.Truthy(stateSfp) ? stateSfp : null).Where(light => EffectDraft.PortEffect(state, draft, Js.ToNumber(light["port"])) is null)
                .Select(light => Js.Num(light["port"])).Where(port => port is not null && port == Math.Floor(port.Value)).Select(port => (int)port!.Value) : [])
            .Distinct().ToList();
        if (brightnessPresent || sfpPorts.Count > 0)
            foreach (var zone in Js.Objects(projected["zones"]))
            {
                var ports = Ports(zone);
                var targets = ports.Where(id => (brightnessPresent || sfpPorts.Contains((int)id) && id == Math.Floor(id)) && Find(stateSfp, id) is not null
                                                 && !Js.Includes(Js.Get(draft, "sfpDetached"), id) && EffectDraft.EffectDraftForPort(draft, id) is null).ToHashSet();
                if (targets.Count > 0) Render(zone["effect"], ports, targets);
            }
        foreach (var port in sfpPorts)
        {
            if (EffectDraft.PortEffect(state, draft, port) is not null) continue;
            var light = SfpSyncModel.EffectiveSfpLight(state, port, draft);
            var mode = Js.StrictEquals(sync["mode"], "ethernet") ? JsonValue.Create("color") : light["mode"];
            var rule = Rules.MatchingSfpRule(rulesOrEmpty, light, mode, date);
            if (rule is not null)
            {
                result[port] = PortDisplay.MasterDisplayRgb(Find(stateSfp, port), JsonValue.Create("color"), master * 100, stateBrightness, rules, gamma, date);
                continue;
            }
            if (Js.StrictEquals(sync["mode"], "ethernet"))
            {
                var sourcePort = sync["source_port"];
                var source = Js.Objects(statePorts).FirstOrDefault(item => Js.StrictEquals(item["port"], sourcePort));
                result[port] = PortKey(sourcePort, out var sourceKey) && result.TryGetValue(sourceKey, out var shown) ? shown
                    : PortDisplay.PortDisplayRgb(source ?? new JsonObject(), gamma);
                continue;
            }
            if (!Js.StrictEquals(light["mode"], "color"))
            {
                result[port] = Rules.MatchingSfpRule(rulesOrEmpty, light, date) is not null ? PortDisplay.PortDisplayRgb(light, gamma) : [0, 0, 0];
                continue;
            }
            result[port] = EffectGallery.SfpPreviewColor(light, Js.Str(light["effect"]), time, master * 100, gamma, SfpSyncModel.SfpNativeAvailable(state));
        }
        return result;
    }
}
