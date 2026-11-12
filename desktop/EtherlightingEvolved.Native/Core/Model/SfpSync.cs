// Port of preview/sfp-sync.mjs: SFP indicator settings with synchronization (independent, together, follow Ethernet),
// native fade periods, and which SFP ports a draft previews. The class is named SfpSyncModel because the module
// exports a function named sfpSync.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class SfpSyncModel
{
    static readonly string[] SharedKeys = ["mode", "color", "brightness", "effect", "secondary_color", "period", "color_brightness", "secondary_brightness"];

    static readonly Dictionary<string, double[]> NativePeriods = new(StringComparer.Ordinal)
    {
        ["pulse"] = [2, 4, 6],
        ["two_color"] = [2, 4, 6],
        ["color_cycle"] = [6, 12, 18],
    };

    /// <summary>sfpSync(state, draft): {mode:'independent', source_port:1} with the state's and the draft's settings over it.</summary>
    public static JsonObject SfpSync(JsonNode? state = null, JsonNode? draft = null) =>
        Js.Spread(new JsonObject { ["mode"] = "independent", ["source_port"] = 1 }, Js.Get(state, "sfp_sync"), Js.Get(draft, "sfp_sync"));

    /// <summary>The SFP lights of a state (an array) or a saved scene (an object keyed by port), with valid port IDs.</summary>
    internal static List<JsonObject> SfpLights(JsonNode? state)
    {
        var sfp = Js.Get(state, "sfp");
        IEnumerable<JsonObject> lights = sfp is JsonArray array ? array.OfType<JsonObject>()
            : Js.Entries(Js.Truthy(sfp) ? sfp : null).Select(entry =>
            {
                var light = Js.Spread(entry.Value);
                light["port"] = Js.StringToNumber(entry.Key);
                return light;
            });
        return lights.Where(light => Js.IsInteger(light["port"]) && Js.Num(light["port"]) > 0).ToList();
    }

    internal static List<int> SfpIds(JsonNode? state) => SfpLights(state).Select(light => (int)Js.Num(light["port"])!.Value).Distinct().Order().ToList();

    /// <summary>sfpLight(state, port, draft): the port's light with its draft changes.</summary>
    public static JsonObject SfpLight(JsonNode? state, double port, JsonNode? draft = null)
    {
        var light = SfpLights(state).FirstOrDefault(item => Js.StrictEquals(item["port"], port));
        var result = Js.Spread(light, Js.Get(Js.Get(draft, "sfp"), Js.Key(port)));
        result["port"] = port;
        return result;
    }

    /// <summary>effectiveSfpLight(state, port, draft): with Together, the lowest SFP port's settings over this port's own light.</summary>
    public static JsonObject EffectiveSfpLight(JsonNode? state, double port, JsonNode? draft = null)
    {
        var sync = SfpSync(state, draft);
        var target = SfpLight(state, port, draft);
        var ids = SfpIds(state);
        if (!Js.StrictEquals(sync["mode"], "together") || !ids.Contains((int)port) || port != Math.Floor(port)) return target;
        var shared = SfpLight(state, ids[0], draft);
        foreach (var key in SharedKeys) Js.SetOrRemove(target, key, Js.Clone(shared[key]), shared[key] is not null || shared.ContainsKey(key) && shared[key] is null);
        return target;
    }

    /// <summary>sfpEditTargets(state, ports, draft): the SFP lights an edit of the selected ports changes.</summary>
    public static List<int> SfpEditTargets(JsonNode? state, IEnumerable<JsonNode?>? ports, JsonNode? draft = null)
    {
        var ids = SfpIds(state);
        var selected = Js.Unique((ports ?? []).Where(port => Js.Num(port) is { } n && ids.Contains((int)n) && n == Math.Floor(n)))
            .Select(port => (int)Js.Num(port)!.Value).ToList();
        if (selected.Count == 0) return [];
        return Js.StrictEquals(SfpSync(state, draft)["mode"], "together") ? [ids[0]] : selected;
    }

    public static List<int> SfpEditTargets(JsonNode? state, IEnumerable<int> ports, JsonNode? draft = null) =>
        SfpEditTargets(state, ports.Select(port => (JsonNode?)JsonValue.Create(port)), draft);

    public static List<int> SfpEditTargets(JsonNode? state, JsonArray ports, JsonNode? draft = null) => SfpEditTargets(state, (IEnumerable<JsonNode?>)ports, draft);

    /// <summary>editableSfpLight(state, port, draft): the effective light with its legacy brightness folded into both colors.</summary>
    public static JsonObject EditableSfpLight(JsonNode? state, double port, JsonNode? draft = null) =>
        NormalizeSfpBrightness(EffectiveSfpLight(state, port, draft));

    /// <summary>normalizeSfpBrightness(light): brightness 100 with the old level multiplied into each color's brightness.</summary>
    public static JsonObject NormalizeSfpBrightness(JsonNode? light)
    {
        double Level(string key) => Js.Get(light, key) is { } value ? Js.ToNumber(value) : 100;
        var scale = Level("brightness") / 100;
        var result = Js.Spread(light);
        result["brightness"] = 100;
        result["color_brightness"] = Js.Round(Level("color_brightness") * scale);
        result["secondary_brightness"] = Js.Round(Level("secondary_brightness") * scale);
        return result;
    }

    /// <summary>sfpNativeAvailable(state): the switch plays SFP fades natively and the engine reports playback.</summary>
    public static bool SfpNativeAvailable(JsonNode? state) =>
        Js.IsTrue(Js.Get(Js.Get(state, "switch"), "sfp_native_gradient")) && SfpLights(state).Any(light => Js.Truthy(light["playback"]));

    /// <summary>sfpNativePeriod(effect, period): the period is one the switch plays natively for the effect.</summary>
    public static bool SfpNativePeriod(string? effect, JsonNode? period) =>
        effect is not null && NativePeriods.TryGetValue(effect, out var stops) && Js.Num(period) is { } value && stops.Contains(value);

    public static bool SfpNativePeriod(string? effect, double period) => SfpNativePeriod(effect, JsonValue.Create(period));

    /// <summary>sfpPeriodOptions(effect, currentPeriod): the native periods, plus a saved custom period.</summary>
    public static JsonArray SfpPeriodOptions(string? effect, JsonNode? currentPeriod)
    {
        var options = new JsonArray();
        if (effect is not null && NativePeriods.TryGetValue(effect, out var stops))
            foreach (var value in stops) options.Add(new JsonObject { ["value"] = value, ["label"] = $"{Js.NumberToString(value)} s" });
        if (Js.IsFinite(currentPeriod) && Js.Num(currentPeriod) is >= 2 and <= 30 && !SfpNativePeriod(effect, currentPeriod))
            options.Add(new JsonObject { ["value"] = Js.Clone(currentPeriod), ["label"] = $"Custom {Js.ToStr(currentPeriod)} s" });
        return options;
    }

    public static JsonArray SfpPeriodOptions(string? effect, double currentPeriod) => SfpPeriodOptions(effect, JsonValue.Create(currentPeriod));

    /// <summary>sfpDurationRange(effect, period, native = true): the duration slider's range and the nearest native stop.</summary>
    public static JsonObject SfpDurationRange(string? effect, JsonNode? period, bool native = true)
    {
        var stops = native && effect is not null && NativePeriods.TryGetValue(effect, out var found) ? found : null;
        var current = Js.IsFinite(period) ? Js.Num(period)!.Value : 6;
        if (stops is null) return new JsonObject { ["min"] = 2, ["max"] = 30, ["step"] = .1, ["value"] = current, ["savedCustom"] = false };
        var value = stops.Aggregate((best, item) => Math.Abs(item - current) < Math.Abs(best - current) ? item : best);
        return new JsonObject { ["min"] = stops[0], ["max"] = stops[^1], ["step"] = stops[1] - stops[0], ["value"] = value, ["savedCustom"] = !stops.Contains(current) };
    }

    public static JsonObject SfpDurationRange(string? effect, double period, bool native = true) => SfpDurationRange(effect, JsonValue.Create(period), native);

    /// <summary>sfpEffectChange(light, effect, native = true): the new effect, with a period the switch can play.</summary>
    public static JsonObject SfpEffectChange(JsonNode? light, string effect, bool native = true)
    {
        var period = Js.Get(light, "period");
        var keep = !native || Js.StrictEquals(Js.Get(light, "effect"), effect) || effect == "steady" || SfpNativePeriod(effect, period);
        var result = new JsonObject { ["effect"] = effect };
        if (!keep) result["period"] = 6;
        else if (Js.Has(light, "period")) result["period"] = Js.Clone(period);
        return result;
    }

    /// <summary>sfpCandidateSettings(state, port, effect, draft): the light a gallery card previews; Follow Ethernet previews as Together.</summary>
    public static JsonObject SfpCandidateSettings(JsonNode? state, double port, string effect, JsonNode? draft = null)
    {
        var sync = SfpSync(state, draft);
        JsonNode? candidate = draft;
        if (Js.StrictEquals(sync["mode"], "ethernet"))
        {
            var changed = Js.Spread(draft);
            var together = Js.Clone(sync);
            together["mode"] = "together";
            changed["sfp_sync"] = together;
            candidate = changed;
        }
        var light = EffectiveSfpLight(state, port, candidate);
        return Js.Assign(light, SfpEffectChange(light, effect, SfpNativeAvailable(state)));
    }

    /// <summary>sfpDraftPorts(state, draft): the SFP ports whose preview the draft changes.</summary>
    public static List<int> SfpDraftPorts(JsonNode? state, JsonNode? draft = null)
    {
        var sync = SfpSync(state, draft);
        var ids = SfpIds(state);
        var changed = Js.Keys(Js.Truthy(Js.Get(draft, "sfp")) ? Js.Get(draft, "sfp") : null).Select(Js.StringToNumber)
            .Where(port => ids.Contains((int)port) && port == Math.Floor(port)).Select(port => (int)port).ToList();
        var detached = Js.Items(Js.Truthy(Js.Get(draft, "sfpDetached")) ? Js.Get(draft, "sfpDetached") : null)
            .Where(id => Js.Num(id) is { } n && n == Math.Floor(n) && ids.Contains((int)n)).Select(id => (int)Js.Num(id)!.Value).ToList();
        var synced = ids.Where(port => EffectDraft.PortEffect(state, draft, port) is null || changed.Contains(port)).ToList();
        if (Js.Truthy(Js.Get(draft, "sfp_sync"))) return synced;
        if (Js.StrictEquals(sync["mode"], "together") && ids.Count > 0 && changed.Contains(ids[0])) return synced;
        if (Js.StrictEquals(sync["mode"], "ethernet"))
        {
            var source = sync["source_port"];
            if (Js.Truthy(Js.Get(Js.Get(draft, "colors"), Js.ToStr(source)))
                || EffectDraft.EffectDrafts(draft).Any(effect => Js.Includes(Js.Get(effect, "ports"), source))
                || Js.Includes(Js.Get(draft, "returnToEffect"), source)) return synced;
        }
        var changedPorts = Js.StrictEquals(sync["mode"], "independent") ? changed
            : changed.Where(id => Js.Objects(Js.Get(state, "zones")).Any(zone => Js.Includes(zone["ports"], id))).ToList();
        return changedPorts.Concat(detached).Distinct().ToList();
    }
}
