// Port of preview/lighting-state.mjs: who controls each port, fixed-color settings, the validated list of requests that
// applies a lighting draft (lightingOperations), the scene those requests produce (lightingSnapshot) and applying the
// plan one request at a time. Effect validation uses the bundled catalog (Effects.Default), as the module does.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class LightingState
{
    static readonly string[] SfpSettingKeys = ["mode", "color", "brightness", "effect", "secondary_color", "period", "color_brightness", "secondary_brightness"];

    /// <summary>copy(value): structuredClone.</summary>
    public static JsonNode? Copy(JsonNode? value) => Js.Clone(value);

    /// <summary>Object.is plus structural comparison of plain objects and arrays (same in the module).</summary>
    static bool Same(JsonNode? first, JsonNode? second)
    {
        if (first is null || second is null) return first is null && second is null;
        if (first is JsonValue && second is JsonValue)
        {
            if (Js.Num(first) is { } a && Js.Num(second) is { } b) return a.Equals(b);
            return Js.StrictEquals(first, second);
        }
        if (first is JsonArray x && second is JsonArray y)
            return x.Count == y.Count && Enumerable.Range(0, x.Count).All(i => Same(x[i], y[i]));
        if (first is JsonObject o && second is JsonObject p)
            return o.Count == p.Count && o.All(pair => p.ContainsKey(pair.Key) && Same(pair.Value, p[pair.Key]));
        return false;
    }

    /// <summary>equalSetting(values): at least one value and all of them equal.</summary>
    public static bool EqualSetting(IEnumerable<JsonNode?> values)
    {
        var list = values.ToList();
        return list.Count > 0 && list.All(value => Same(value, list[0]));
    }

    public static bool EqualSetting(JsonArray values) => EqualSetting((IEnumerable<JsonNode?>)values);

    static JsonNode? Find(JsonNode? list, double port) => Js.Objects(list).FirstOrDefault(item => Js.StrictEquals(item["port"], port));

    /// <summary>
    /// sceneAutomationMatches(scene, state, portIds, date = hostScheduleDate(state)): the rules an accepted scene runs on
    /// the given ports with the current facts.
    /// </summary>
    public static JsonArray SceneAutomationMatches(JsonNode? scene, JsonNode? state, IEnumerable<JsonNode?> portIds) =>
        SceneAutomationMatches(scene, state, portIds, ScheduleClock.HostDate(state));

    public static JsonArray SceneAutomationMatches(JsonNode? scene, JsonNode? state, IEnumerable<JsonNode?> portIds, DateTime? date)
    {
        var rules = Js.Truthy(Js.Get(scene, "rules")) ? Js.Get(scene, "rules") : new JsonArray();
        var sync = Js.Truthy(Js.Get(scene, "sfp_sync")) ? Js.Get(scene, "sfp_sync") : new JsonObject { ["mode"] = "independent", ["source_port"] = 1 };
        var sceneSfp = Js.Get(scene, "sfp");
        var sfp = new JsonArray();
        foreach (var port in Js.Objects(Js.Truthy(Js.Get(state, "sfp")) ? Js.Get(state, "sfp") : null))
        {
            var key = Js.ToStr(port["port"]);
            if (!Js.Has(sceneSfp, key)) continue;
            var light = Js.Spread(port, Js.Get(sceneSfp, key));
            light["port"] = Js.Clone(port["port"]);
            sfp.Add(light);
        }
        var projected = new JsonObject { ["sfp"] = sfp, ["sfp_sync"] = Js.Clone(sync) };
        var matches = new JsonArray();
        var ids = Js.Unique(portIds.Select(Js.ToNumber)).Order().ToList();
        foreach (var id in ids)
        {
            var light = Find(sfp, id);
            var port = light ?? Find(Js.Get(state, "ports"), id);
            if (port is null || light is null && Js.Has(Js.Truthy(Js.Get(scene, "overrides")) ? Js.Get(scene, "overrides") : null, id)) continue;
            JsonObject? rule;
            if (light is not null)
            {
                var grouped = Js.Objects(Js.Get(scene, "zones")).Any(zone => Js.Includes(zone["ports"], id)) || Js.StrictEquals(Js.Get(sync, "mode"), "ethernet");
                var mode = grouped ? JsonValue.Create("color") : Js.Clone(SfpSyncModel.EffectiveSfpLight(projected, id)["mode"]);
                rule = Rules.MatchingSfpRule(rules, light, mode, date);
            }
            else rule = Rules.MatchingRule(rules, id, Js.Truthy(Js.Get(port, "facts")) ? Js.Get(port, "facts") : new JsonObject(), date);
            if (rule is not null) matches.Add(new JsonObject { ["port"] = id, ["rule"] = rule.DeepClone() });
        }
        return matches;
    }

    public static JsonArray SceneAutomationMatches(JsonNode? scene, JsonNode? state, IEnumerable<int> portIds) =>
        SceneAutomationMatches(scene, state, portIds.Select(id => (JsonNode?)JsonValue.Create(id)));

    static readonly Dictionary<string, string> SharedLabels = new(StringComparer.Ordinal)
    {
        ["steady"] = "color", ["pulse"] = "Gentle pulse", ["two_color"] = "Two-color fade", ["color_cycle"] = "Color cycle",
    };

    static readonly Dictionary<string, string> SfpLabels = new(StringComparer.Ordinal)
    {
        ["steady"] = "Steady", ["pulse"] = "Gentle pulse", ["two_color"] = "Two-color fade", ["color_cycle"] = "Color cycle",
    };

    static JsonObject Source(string kind, string? label, string? key = null, JsonNode? value = null)
    {
        var result = new JsonObject { ["kind"] = kind };
        if (label is not null) result["label"] = label;
        if (key is not null) result[key] = Js.Clone(value);
        return result;
    }

    /// <summary>controlSource(state, id, effects = [], date = hostScheduleDate(state)): {kind, label} for what controls a port.</summary>
    public static JsonObject ControlSource(JsonNode? state, double id, JsonNode? effects = null) =>
        ControlSource(state, id, effects, ScheduleClock.HostDate(state));

    public static JsonObject ControlSource(JsonNode? state, double id, JsonNode? effects, DateTime? date)
    {
        var sfp = Js.Objects(Js.Get(state, "sfp")).Any(port => Js.StrictEquals(port["port"], id));
        var port = Find(sfp ? Js.Get(state, "sfp") : Js.Get(state, "ports"), id) as JsonObject;
        if (port is null) return Source("unknown", "Waiting for status");
        if (!sfp && (Js.Truthy(Js.Get(Js.Get(state, "overrides"), Js.Key(id))) || Js.Truthy(port["override"])
                     || Js.StrictEquals(Js.Get(port["source"], "kind"), "override"))) return Source("color", "Fixed color");
        var sync = Js.Truthy(Js.Get(state, "sfp_sync")) ? Js.Get(state, "sfp_sync") : new JsonObject { ["mode"] = "independent", ["source_port"] = 1 };
        var syncMode = Js.Get(sync, "mode");
        var shared = sfp ? SfpSyncModel.EffectiveSfpLight(state, id) : null;
        var grouped = sfp ? EffectDraft.PortEffect(state, new JsonObject(), id) : null;
        if (grouped is null && sfp && Js.StrictEquals(syncMode, "independent") && Js.StrictEquals(port["mode"], "off")) return Source("off", "Off");
        if (grouped is null && sfp && Js.StrictEquals(syncMode, "together") && Js.StrictEquals(shared!["mode"], "off")) return Source("off", "SFP lights off");
        var reportedRule = sfp ? port["rule"] : Js.StrictEquals(Js.Get(port["source"], "kind"), "rule") ? port["source"] : null;
        var reported = sfp ? port.ContainsKey("rule") : Js.Truthy(port["source"]);
        JsonNode? ruleMode = grouped is not null ? JsonValue.Create("color")
            : sfp && !Js.StrictEquals(syncMode, "independent") ? Js.StrictEquals(syncMode, "ethernet") ? JsonValue.Create("color") : shared!["mode"]
            : port["mode"];
        var rules = Js.Truthy(Js.Get(state, "rules")) ? Js.Get(state, "rules") : new JsonArray();
        JsonNode? rule = Js.Truthy(reportedRule)
            ? (JsonNode?)Js.Objects(Js.Get(state, "rules")).FirstOrDefault(item => Js.StrictEquals(item["id"], Js.Get(reportedRule, "id"))) ?? reportedRule
            : reported ? null
            : sfp ? Rules.MatchingSfpRule(rules, port, ruleMode, date)
            : Rules.MatchingRule(rules, id, Js.Truthy(port["facts"]) ? port["facts"] : new JsonObject(), date);
        if (Js.Truthy(rule))
        {
            var result = new JsonObject { ["kind"] = "rule" };
            if (Js.Has(rule, "name")) result["label"] = Js.Clone(Js.Get(rule, "name"));
            result["rule"] = Js.Clone(rule);
            return result;
        }
        if (grouped is not null)
            return Source("effect", EffectName(effects, grouped["id"]) ?? "Effect", "effect", grouped);
        if (sfp && Js.StrictEquals(syncMode, "ethernet")) return Source("sync", $"Following port {Js.ToStr(Js.Get(sync, "source_port"))}");
        if (sfp && Js.StrictEquals(syncMode, "together"))
        {
            if (Js.StrictEquals(shared!["mode"], "off")) return Source("off", "SFP lights off");
            if (Js.StrictEquals(shared["mode"], "unmanaged")) return Source("link", "Link status");
            return Source("sfp", $"Shared {(Js.Str(shared["effect"]) is { } key && SharedLabels.TryGetValue(key, out var label) ? label : "effect")}");
        }
        if (sfp)
        {
            var mode = port["mode"];
            if (Js.StrictEquals(mode, "off")) return Source("off", "Off");
            if (Js.StrictEquals(mode, "color"))
                return Source("sfp", Js.Str(port["effect"]) is { } key && SfpLabels.TryGetValue(key, out var label) ? label : "Custom color");
            return Source("link", "Link status");
        }
        if (Js.StrictEquals(Js.Get(port["source"], "kind"), "none")) return Source("off", "Off");
        var effect = Js.Objects(Js.Get(state, "zones")).FirstOrDefault(zone => Js.Includes(zone["ports"], id))?["effect"];
        if (!Js.Truthy(effect)) return Source("off", "Off");
        return Source("effect", EffectName(effects, Js.Get(effect, "id")) ?? "Effect", "effect", effect);
    }

    static string? EffectName(JsonNode? effects, JsonNode? id) =>
        Js.Objects(effects).FirstOrDefault(item => Js.StrictEquals(item["id"], id))?["name"] is { } name && Js.Truthy(name) ? Js.ToStr(name) : null;

    /// <summary>sfpSettings(item): the SFP lighting settings of a light, without telemetry.</summary>
    public static JsonObject SfpSettings(JsonNode? item)
    {
        var result = new JsonObject();
        foreach (var key in SfpSettingKeys) if (Js.Has(item, key)) result[key] = Js.Clone(Js.Get(item, key));
        return result;
    }

    /// <summary>fixedColorSettings(state, id, draft): {color, brightness} the fixed-color controls edit for a port.</summary>
    public static JsonObject FixedColorSettings(JsonNode? state, double id, JsonNode? draft = null)
    {
        if (Js.Objects(Js.Get(state, "sfp")).Any(port => Js.StrictEquals(port["port"], id)))
        {
            var light = SfpSyncModel.EditableSfpLight(state, id, draft);
            var result = new JsonObject();
            Js.SetOrRemove(result, "color", Js.Clone(light["color"]), light.ContainsKey("color"));
            result["brightness"] = Js.Clone(light["color_brightness"]);
            return result;
        }
        var port = Find(Js.Get(state, "ports"), id) ?? throw new InvalidOperationException("Choose an available port.");
        var key = Js.Key(id);
        var chosen = Js.Get(Js.Get(draft, "colors"), key) ?? Js.Get(Js.Get(state, "overrides"), key);
        if (chosen is not null) return chosen is JsonObject settings ? Js.Clone(settings) : new JsonObject();
        return new JsonObject { ["color"] = Js.Truthy(Js.Get(port, "color")) ? Js.Clone(Js.Get(port, "color")) : "#ffffff", ["brightness"] = null };
    }

    /// <summary>fixedColorBrightness(settings, state, draft): the brightness a fixed color shows at.</summary>
    public static double FixedColorBrightness(JsonNode? settings, JsonNode? state, JsonNode? draft = null)
    {
        var value = Js.Get(settings, "brightness") ?? Js.Get(draft, "brightness") ?? Js.Get(state, "brightness");
        return value is null ? 100 : Js.ToNumber(value);
    }

    static void Fields(JsonNode? value, string[] allowed, string message)
    {
        if (value is not JsonObject obj || obj.Any(pair => !allowed.Contains(pair.Key))) throw new InvalidOperationException(message);
    }

    static void Range(JsonNode? value, double min, double max, string message, bool integer = false)
    {
        if (Js.Num(value) is not { } number || !double.IsFinite(number) || number < min || number > max || integer && !Js.IsInteger(number))
            throw new InvalidOperationException(message);
    }

    static bool IsColor(JsonNode? value) => Js.Str(value) is { Length: 7 } text && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit);

    static void ValidateEffect(JsonNode? effect)
    {
        Fields(effect, ["id", "params", "ports", "zoneId", "clearOverrides"], "Choose valid effect settings.");
        var obj = (JsonObject)effect!;
        var spec = Js.Objects(Effects.Default.EffectList).FirstOrDefault(item => Js.StrictEquals(item["id"], obj["id"]))
                   ?? throw new InvalidOperationException("Choose an available lighting effect.");
        if (obj.ContainsKey("zoneId") && (Js.Str(obj["zoneId"]) is not { } zone || Js.Trim(zone).Length == 0))
            throw new InvalidOperationException("Choose a valid effect group.");
        if (obj.ContainsKey("clearOverrides") && !Js.IsBoolean(obj["clearOverrides"]))
            throw new InvalidOperationException("Choose whether to replace fixed port colors.");
        var parameters = obj.ContainsKey("params") ? obj["params"] : new JsonObject();
        var specs = Js.Objects(spec["params"]).ToList();
        Fields(parameters, specs.Select(param => Js.ToStr(param["key"])).ToArray(), "Choose valid effect parameters.");
        foreach (var (key, value) in Js.Entries(parameters))
        {
            var param = specs.First(item => Js.ToStr(item["key"]) == key);
            var message = $"Choose a valid {(Js.Truthy(param["label"]) ? Js.ToStr(param["label"]) : key)} value.";
            var type = Js.Str(param["type"]);
            if (type == "color") { if (!IsColor(value)) throw new InvalidOperationException(message); }
            else if (type == "bool") { if (!Js.IsBoolean(value)) throw new InvalidOperationException(message); }
            else if (type == "choice") { if (!Js.Includes(param["choices"], value)) throw new InvalidOperationException(message); }
            else if (type is "int" or "float")
                Range(value, param["min"] is { } min ? Js.ToNumber(min) : double.NegativeInfinity, param["max"] is { } max ? Js.ToNumber(max) : double.PositiveInfinity,
                    message, type == "int");
            else throw new InvalidOperationException(message);
        }
        foreach (var pair in Js.Items(spec["exclusive_params"]))
        {
            var left = Js.ToStr(Js.Get(pair, 0));
            var right = Js.ToStr(Js.Get(pair, 1));
            if (Js.IsTrue(Js.Get(parameters, left)) && Js.IsTrue(Js.Get(parameters, right)))
                throw new InvalidOperationException("Choose one of the conflicting color options.");
        }
    }

    static void ValidateSfp(JsonNode? settings)
    {
        Fields(settings, ["mode", "color", "brightness", "effect", "secondary_color", "period", "color_brightness", "secondary_brightness"], "Choose valid SFP lighting settings.");
        foreach (var (key, value) in Js.Entries(settings))
        {
            if (key is "color" or "secondary_color") { if (!IsColor(value)) throw new InvalidOperationException("Enter a six-digit SFP color."); }
            else if (key is "brightness" or "color_brightness" or "secondary_brightness")
                Range(value, 0, 100, "SFP brightness must be a whole number from 0 to 100.", true);
            else if (key == "period") Range(value, 2, 30, "SFP duration must be from 2 to 30 seconds.");
            else if (key == "mode" && Js.Str(value) is not ("unmanaged" or "color" or "off")) throw new InvalidOperationException("Choose an available SFP lighting mode.");
            else if (key == "effect" && Js.Str(value) is not ("steady" or "pulse" or "two_color" or "color_cycle"))
                throw new InvalidOperationException("Choose an available SFP lighting effect.");
        }
    }

    static JsonObject Operation(string key, string label, string path, string method, JsonNode? body = null)
    {
        var operation = new JsonObject { ["key"] = key, ["label"] = label, ["path"] = path, ["method"] = method };
        if (body is not null) operation["body"] = body;
        return operation;
    }

    /// <summary>
    /// lightingOperations(state, draft): the requests that apply a draft, in order: brightness, group changes and
    /// effects, return-to-effect, fixed colors, SFP lights, SFP sync, lighting mode. Throws on an invalid draft.
    /// Each operation is {key, label, path, method, body}.
    /// </summary>
    public static JsonArray LightingOperations(JsonNode? state, JsonNode? draft) =>
        Operations(state, draft, Js.Get(state, "zones"), Js.Get(state, "brightness"), Js.Get(state, "sfp_sync"));

    static JsonArray Operations(JsonNode? state, JsonNode? draftNode, JsonNode? stateZones, JsonNode? stateBrightness, JsonNode? stateSync)
    {
        Fields(draftNode, ["brightness", "effect", "effects", "returnToEffect", "colors", "sfp", "sfp_sync", "sfpDetached", "mode"], "Choose valid lighting changes.");
        var draft = (JsonObject)draftNode!;
        var operations = new JsonArray();
        void Add(string key, string label, string path, string method, JsonNode? body = null) => operations.Add(Operation(key, label, path, method, body));
        var topology = PortTopology.LightingPorts(state);
        List<double> CopperPorts(JsonNode? values)
        {
            if (values is not JsonArray list || list.Any(port => !(Js.Num(port) is { } n && topology.Copper.Contains((int)n) && n == Math.Floor(n))))
                throw new InvalidOperationException("Choose Ethernet ports for this change.");
            return Js.Unique(list.Select(port => Js.Num(port)!.Value)).Order().ToList();
        }
        List<double> CopperNumbers(IEnumerable<double> values)
        {
            var list = values.ToList();
            if (list.Any(n => !(topology.Copper.Contains((int)n) && n == Math.Floor(n)))) throw new InvalidOperationException("Choose Ethernet ports for this change.");
            return Js.Unique(list).Order().ToList();
        }
        if (draft.ContainsKey("brightness"))
        {
            Range(draft["brightness"], 0, 100, "Brightness must be a whole number from 0 to 100.", true);
            if (!Js.StrictEquals(draft["brightness"], stateBrightness))
                Add("brightness", "Default brightness", "/api/brightness", "PUT", new JsonObject { ["value"] = Js.Clone(draft["brightness"]) });
        }
        if (draft.ContainsKey("effects") && (draft["effects"] is not JsonArray effectsList || effectsList.Count > 64))
            throw new InvalidOperationException("Choose valid effect previews.");
        if (draft.ContainsKey("effect")) ValidateEffect(draft["effect"]);
        // The zone objects are kept by reference, as in the module: a later effect assignment also updates the zones
        // body of an earlier group request that contains the same objects.
        var effectZones = Js.Objects(Js.Clone(Js.Truthy(stateZones) ? stateZones : new JsonArray())).ToList();
        var available = topology.Copper.Concat(topology.Sfp).ToHashSet();
        bool Available(JsonNode? port) => Js.Num(port) is { } n && n == Math.Floor(n) && available.Contains((int)n);
        bool InSet(IEnumerable<JsonNode?> set, JsonNode? port) => set.Any(item => Js.SameValueZero(item, port));
        if (draft.ContainsKey("sfpDetached"))
        {
            if (draft["sfpDetached"] is not JsonArray detachedList
                || detachedList.Any(port => !(Js.Num(port) is { } n && n == Math.Floor(n) && topology.Sfp.Contains((int)n))))
                throw new InvalidOperationException("Choose an available SFP port.");
            var detached = detachedList.ToList();
            if (effectZones.Any(zone => Js.Items(zone["ports"]).Any(port => InSet(detached, port))))
            {
                effectZones = effectZones.Select(zone =>
                {
                    var copy = Js.Spread(zone);
                    copy["ports"] = Js.ArrayOf(Js.Items(zone["ports"]).Where(port => !InSet(detached, port)));
                    return copy;
                }).Where(zone => Js.Length(zone["ports"]) > 0).ToList();
                Add("group", "Return to SFP lighting", "/api/zones", "PUT", new JsonObject { ["zones"] = Js.ArrayOf(effectZones) });
            }
        }
        foreach (var effectDraft in EffectDraft.EffectDrafts(draft))
        {
            ValidateEffect(effectDraft);
            var draftPorts = Js.Get(effectDraft, "ports");
            if (draftPorts is not JsonArray portList || portList.Any(port => !Available(port)))
                throw new InvalidOperationException("Choose available lighting ports for this effect.");
            var ports = Js.Unique(portList.Select(port => Js.Num(port)!.Value)).Order().ToList();
            var parameters = Js.Get(effectDraft, "params");
            var effect = new JsonObject
            {
                ["id"] = Js.Clone(Js.Get(effectDraft, "id")),
                ["params"] = Js.Truthy(parameters) ? Js.Clone(parameters) : new JsonObject(),
            };
            if (ports.Count == 0) throw new InvalidOperationException("Select at least one port.");
            var exact = effectZones.FirstOrDefault(zone => Js.Length(zone["ports"]) == ports.Count && Js.Items(zone["ports"]).All(port => Js.Num(port) is { } n && ports.Contains(n)));
            var id = exact?["id"];
            if (!Js.Truthy(id))
            {
                var zones = effectZones.Select(zone =>
                {
                    var copy = Js.Clone(zone);
                    copy["ports"] = Js.ArrayOf(Js.Items(zone["ports"]).Where(port => !(Js.Num(port) is { } n && ports.Contains(n))));
                    return copy;
                }).Where(zone => Js.Length(zone["ports"]) > 0).ToList();
                var zoneId = Js.Get(effectDraft, "zoneId");
                var baseId = Js.Truthy(zoneId) ? Js.ToStr(zoneId) : $"selection-{string.Join("-", ports.Select(Js.NumberToString))}";
                var used = zones.Select(zone => zone["id"]).ToList();
                var next = baseId;
                for (var suffix = 2; used.Any(item => Js.StrictEquals(item, next)); suffix++) next = $"{baseId}-{suffix}";
                id = JsonValue.Create(next);
                zones.Add(new JsonObject
                {
                    ["id"] = next, ["name"] = $"Ports {string.Join(", ", ports.Select(Js.NumberToString))}", ["ports"] = Js.NumberArray(ports),
                    ["effect"] = effect.DeepClone(),
                });
                var body = new JsonArray();
                foreach (var zone in zones) body.Add(zone);
                effectZones = zones;
                Add("group", "Selected port group", "/api/zones", "PUT", new JsonObject { ["zones"] = body });
            }
            var target = effectZones.First(zone => Js.StrictEquals(zone["id"], id));
            target["effect"] = effect.DeepClone();
            var clear = Js.Get(effectDraft, "clearOverrides");
            var request = Js.Spread(effect);
            request["clear_overrides"] = Js.IsTrue(clear);
            Add("effect", Js.Truthy(clear) ? "Effect and selected port colors" : "Effect",
                $"/api/zones/{Js.EncodeURIComponent(Js.ToStr(id))}/effect", "PUT", request);
        }
        var returning = draft["returnToEffect"];
        foreach (var port in CopperPorts(returning ?? new JsonArray()))
        {
            var text = Js.NumberToString(port);
            Add($"return:{text}", $"Port {text}: follow effect", $"/api/ports/{text}", "DELETE");
        }
        if (draft.ContainsKey("returnToEffect") && returning is not JsonArray) throw new InvalidOperationException("Choose Ethernet ports for this change.");
        if (draft.ContainsKey("colors"))
        {
            if (draft["colors"] is not JsonObject colorMap) throw new InvalidOperationException("Choose valid fixed port colors.");
            if (Js.Keys(colorMap).Any(port => Js.NumberToString(Js.StringToNumber(port)) != port)) throw new InvalidOperationException("Choose Ethernet ports for this change.");
            foreach (var settings in Js.Values(colorMap))
            {
                Fields(settings, ["color", "brightness"], "Choose valid fixed port settings.");
                if (!IsColor(Js.Get(settings, "color"))) throw new InvalidOperationException("Enter a six-digit port color.");
                if (Js.Get(settings, "brightness") is not null)
                    Range(Js.Get(settings, "brightness"), 0, 100, "Port brightness must be a whole number from 0 to 100.", true);
            }
        }
        if (Js.Keys(Js.Truthy(draft["colors"]) ? draft["colors"] : null).Count > 0)
        {
            CopperNumbers(Js.Keys(draft["colors"]).Select(Js.StringToNumber));
            Add("colors", "Selected port colors", "/api/ports", "PUT", new JsonObject { ["ports"] = Js.Clone(draft["colors"]) });
        }
        if (draft.ContainsKey("sfp") && draft["sfp"] is not JsonObject) throw new InvalidOperationException("Choose valid SFP lighting changes.");
        foreach (var (port, settings) in Js.Entries(Js.Truthy(draft["sfp"]) ? draft["sfp"] : null))
        {
            var number = Js.StringToNumber(port);
            if (Js.NumberToString(number) != port || !Js.Objects(Js.Get(state, "sfp")).Any(item => Js.StrictEquals(item["port"], number)))
                throw new InvalidOperationException("Choose an available SFP port.");
            ValidateSfp(settings);
            var changes = SfpSettings(settings);
            if (changes.Count > 0) Add($"sfp:{port}", $"SFP {port}", $"/api/sfp/{port}", "PUT", changes);
        }
        if (draft.ContainsKey("sfp_sync"))
        {
            Fields(draft["sfp_sync"], ["mode", "source_port"], "Choose valid SFP sync settings.");
            var sync = Js.Spread(Js.Truthy(stateSync) ? stateSync : new JsonObject { ["mode"] = "independent", ["source_port"] = 1 }, draft["sfp_sync"]);
            if (Js.Str(sync["mode"]) is not ("independent" or "together" or "ethernet")) throw new InvalidOperationException("Choose an available SFP sync mode.");
            var copper = topology.Copper;
            var source = sync["source_port"];
            var validSource = copper.Count > 0 ? Js.Num(source) is { } n && n == Math.Floor(n) && copper.Contains((int)n)
                : !Js.StrictEquals(sync["mode"], "ethernet") && Js.StrictEquals(source, 1);
            if (!PortTopology.ValidPort(source) || !validSource) throw new InvalidOperationException("Choose an Ethernet port to follow.");
            Add("sfp-sync", "SFP synchronization", "/api/sfp/sync", "PUT", sync);
        }
        if (draft.ContainsKey("mode"))
        {
            var mode = Js.Str(draft["mode"]);
            if (mode is not ("running" or "released")) throw new InvalidOperationException("Choose an available lighting mode.");
            Add("mode", mode == "running" ? "Enable EE lighting" : "Return lighting to UniFi", mode == "running" ? "/api/resume" : "/api/release", "POST");
        }
        return operations;
    }

    /// <summary>
    /// lightingSnapshot(baseScene, state, draft): the scene the draft produces from the authoritative base scene,
    /// without changing either input.
    /// </summary>
    public static JsonObject LightingSnapshot(JsonNode? baseScene, JsonNode? state, JsonNode? draft) => Snapshot(baseScene, state, draft, true);

    internal static JsonObject Snapshot(JsonNode? baseScene, JsonNode? state, JsonNode? draft, bool copyBase)
    {
        if (baseScene is not JsonObject baseObject || baseObject["zones"] is not JsonArray || baseObject["rules"] is not JsonArray
            || baseObject["overrides"] is not JsonObject || baseObject["sfp"] is not JsonObject)
            throw new InvalidOperationException("Refresh the lighting setup before applying changes.");
        var scene = copyBase ? Js.Clone(baseObject) : baseObject;
        var operations = Operations(state, draft, scene["zones"], scene["brightness"], scene["sfp_sync"]);
        foreach (var operation in Js.Objects(operations))
        {
            var key = Js.ToStr(operation["key"]);
            var body = operation["body"];
            if (key == "brightness") scene["brightness"] = Js.Clone(Js.Get(body, "value"));
            else if (key == "group") scene["zones"] = Js.Clone(Js.Get(body, "zones"));
            else if (key == "effect")
            {
                var id = Js.DecodeURIComponent(Js.ToStr(operation["path"]).Split('/')[3]);
                var zone = Js.Objects(scene["zones"]).FirstOrDefault(item => Js.StrictEquals(item["id"], id))
                           ?? throw new InvalidOperationException("The selected group changed. Review the current lighting setup.");
                zone["effect"] = new JsonObject { ["id"] = Js.Clone(Js.Get(body, "id")), ["params"] = Js.Clone(Js.Get(body, "params")) };
                if (Js.Truthy(Js.Get(body, "clear_overrides")))
                    foreach (var port in Js.Items(zone["ports"])) ((JsonObject)scene["overrides"]!).Remove(Js.ToStr(port));
            }
            else if (key.StartsWith("return:", StringComparison.Ordinal)) ((JsonObject)scene["overrides"]!).Remove(key[7..]);
            else if (key == "colors") Js.Assign((JsonObject)scene["overrides"]!, Js.Get(body, "ports"));
            else if (key == "sfp-sync") scene["sfp_sync"] = Js.Clone(body);
            else if (key.StartsWith("sfp:", StringComparison.Ordinal))
            {
                var port = key[4..];
                var sfp = (JsonObject)scene["sfp"]!;
                if (sfp[port] is not JsonObject light) throw new InvalidOperationException("The selected SFP light changed. Refresh its settings.");
                sfp[port] = Js.Spread(light, body);
            }
        }
        return scene;
    }
}
