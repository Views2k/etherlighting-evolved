// Port of preview/effect-draft.mjs: the staged effect previews of an unapplied lighting draft. A draft keeps a legacy
// single "effect" and a list of "effects"; later entries take precedence for the ports they cover.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class EffectDraft
{
    /// <summary>effectDrafts(draft): the staged effects (the draft's own nodes), the legacy single effect first.</summary>
    public static List<JsonNode?> EffectDrafts(JsonNode? draft)
    {
        var result = new List<JsonNode?>();
        var effect = Js.Get(draft, "effect");
        if (Js.Truthy(effect)) result.Add(effect);
        var effects = Js.Get(draft, "effects");
        if (Js.Truthy(effects)) result.AddRange(Js.Items(effects));
        return result;
    }

    /// <summary>effectDraftForPort(draft, port): the last staged effect covering the port, or null.</summary>
    public static JsonObject? EffectDraftForPort(JsonNode? draft, double port) =>
        EffectDrafts(draft).OfType<JsonObject>().LastOrDefault(effect => Js.Includes(effect["ports"], port));

    internal static JsonObject? EffectDraftForPort(JsonNode? draft, JsonNode? port) =>
        EffectDrafts(draft).OfType<JsonObject>().LastOrDefault(effect => Js.Includes(effect["ports"], port));

    /// <summary>portEffect(state, draft, port): the staged or applied effect of a port; null for an SFP port leaving its group.</summary>
    public static JsonObject? PortEffect(JsonNode? state, JsonNode? draft, double port)
    {
        if (Js.Includes(Js.Get(draft, "sfpDetached"), port)) return null;
        return EffectDraftForPort(draft, port)
               ?? Js.Objects(Js.Get(state, "zones")).FirstOrDefault(zone => Js.Includes(zone["ports"], port))?["effect"] as JsonObject;
    }

    /// <summary>leaveSfpEffect(draft, ports): removes the SFP ports from staged effects and returns them to SFP lighting.</summary>
    public static JsonObject LeaveSfpEffect(JsonObject draft, IEnumerable<int> ports)
    {
        var leaving = ports.ToList();
        if (leaving.Count == 0) return draft;
        var effects = new JsonArray();
        foreach (var effect in EffectDrafts(draft))
        {
            var copy = Js.Spread(effect);
            copy["ports"] = Js.ArrayOf(Js.Items(Js.Get(effect, "ports")).Where(id => !(Js.Num(id) is { } n && n == Math.Floor(n) && leaving.Contains((int)n))));
            if (Js.Length(copy["ports"]) > 0) effects.Add(copy);
        }
        var result = Js.Spread(draft);
        result["effects"] = effects;
        var detached = Js.Unique(Js.Items(Js.Get(draft, "sfpDetached")).Select(Js.Clone).Concat(leaving.Select(id => (JsonNode?)JsonValue.Create((double)id))));
        result["sfpDetached"] = Js.ArrayOf(detached);
        result.Remove("effect");
        return result;
    }

    static bool Contains(List<JsonNode?> set, JsonNode? value) => set.Any(item => Js.SameValueZero(item, value));

    /// <summary>
    /// stageEffectDraft(draft, config, newId, copperPorts = config.ports): stages an effect for config.ports, taking
    /// those ports out of other staged effects and keeping the group identity and fixed-color choice of an exact match.
    /// </summary>
    public static JsonObject StageEffectDraft(JsonObject draft, JsonObject config, string newId, IEnumerable<int>? copperPorts = null)
    {
        var ports = Js.SortNumeric(Js.Unique(Js.Items(config["ports"])));
        var existing = EffectDrafts(draft);
        var returned = Js.Unique(Js.Items(Js.Truthy(draft["returnToEffect"]) ? draft["returnToEffect"] : null));
        var previous = existing.FirstOrDefault(effect => Js.Get(effect, "ports") is JsonArray list && list.Count == ports.Count && list.All(port => Contains(ports, port)));
        var copperList = copperPorts?.Select(port => (JsonNode?)JsonValue.Create((double)port)).ToList() ?? Js.Items(config["ports"]).ToList();
        var copper = ports.Where(port => Contains(copperList, port)).ToList();
        var inheritedClears = copper.Where(port => Contains(returned, port) || Js.Truthy(Js.Get(EffectDraftForPort(draft, port), "clearOverrides"))).ToList();
        var configClear = config["clearOverrides"];
        var clearPresent = config.ContainsKey("clearOverrides");
        JsonNode clearOverrides = configClear is not null ? configClear.DeepClone() : JsonValue.Create(copper.Count > 0 && inheritedClears.Count == copper.Count);
        var next = Js.Clone(config);
        next["ports"] = Js.ArrayOf(ports);
        next["clearOverrides"] = clearOverrides;
        var previousZone = Js.Get(previous, "zoneId");
        next["zoneId"] = Js.Truthy(previousZone) ? previousZone!.DeepClone() : JsonValue.Create(newId);
        var effects = new JsonArray();
        foreach (var effect in existing)
        {
            var copy = Js.Spread(Js.Clone(effect));
            copy["ports"] = Js.ArrayOf(Js.Items(Js.Get(effect, "ports")).Where(port => !Contains(ports, port)));
            if (Js.Length(copy["ports"]) > 0) effects.Add(copy);
        }
        effects.Add(next);
        var result = Js.Spread(draft);
        result["effects"] = effects;
        result.Remove("effect");
        if (Js.Truthy(result["sfpDetached"])) result["sfpDetached"] = Js.ArrayOf(Js.Items(result["sfpDetached"]).Where(port => !Contains(ports, port)));
        if (Js.IsTrue(configClear))
        {
            var colors = new JsonObject();
            foreach (var (port, value) in Js.Entries(Js.Truthy(draft["colors"]) ? draft["colors"] : null))
                if (!Contains(ports, JsonValue.Create(Js.StringToNumber(port)))) colors[port] = Js.Clone(value);
            result["colors"] = colors;
            result["returnToEffect"] = Js.ArrayOf(Js.Items(Js.Truthy(draft["returnToEffect"]) ? draft["returnToEffect"] : null).Where(port => !Contains(ports, port)));
        }
        else if (!clearPresent && !Js.Truthy(clearOverrides) && inheritedClears.Count > 0)
        {
            var merged = Js.Unique(Js.Items(Js.Truthy(draft["returnToEffect"]) ? draft["returnToEffect"] : null).Concat(inheritedClears));
            result["returnToEffect"] = Js.ArrayOf(Js.SortNumeric(merged));
        }
        return result;
    }
}
