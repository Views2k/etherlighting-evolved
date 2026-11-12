// Port of the logic exports of preview/profile-preview.mjs: which ports a saved profile covers, the layout notice for
// a profile saved on a different switch, and the colors a saved profile shows. createProfilePreviews animates DOM
// swatches and belongs to the Profiles page.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>The animation clocks and per-port states of one previewed group (the animation map entries).</summary>
public sealed class ProfileAnimation
{
    public EffectClock Motion { get; } = new();

    public EffectClock Colors { get; } = new();

    public Dictionary<int, EffectAnimationState> Ports { get; } = [];
}

public static class ProfilePreview
{
    static double Percent(double value) => Math.Max(0, Math.Min(100, double.IsFinite(value) ? value : 100)) / 100;

    static double Percent(JsonNode? value) => Percent(Js.ToNumber(value));

    static IEnumerable<JsonNode?> LightPorts(JsonNode? scene)
    {
        var sfp = Js.Get(scene, "sfp");
        return sfp is JsonArray lights ? lights.Select(light => Js.Get(light, "port"))
            : Js.Keys(Js.Truthy(sfp) ? sfp : null).Select(key => (JsonNode?)JsonValue.Create(Js.StringToNumber(key)));
    }

    /// <summary>profilePorts(scene, state): the saved topology, or for older profiles the ports implied by the switch and the scene.</summary>
    public static PortLayout ProfilePorts(JsonNode? scene, JsonNode? state = null)
    {
        if (PortTopology.SceneTopology(scene) is { } topology) return topology;
        if (Js.ToNumber(Js.Get(scene, "version")) >= 3) return PortLayout.Empty;
        var sfp = PortTopology.PortIds(LightPorts(scene));
        var zones = Js.Get(scene, "zones") is JsonArray list ? list : [];
        var known = PortTopology.PortIds(zones.SelectMany(zone => Js.Truthy(Js.Get(zone, "ports")) ? Js.Items(Js.Get(zone, "ports")) : [])
            .Concat(Js.Keys(Js.Truthy(Js.Get(scene, "overrides")) ? Js.Get(scene, "overrides") : null).Select(key => (JsonNode?)JsonValue.Create(Js.StringToNumber(key)))));
        var count = Js.Get(state, "num_ports") ?? Js.Get(Js.Get(state, "switch"), "num_ports");
        var copperIds = PortTopology.LightingPorts(state).Copper;
        var overlaps = sfp.Any(copperIds.Contains);
        var extent = !overlaps && Js.IsInteger(count) && Js.Num(count) is >= 0 and <= 256 ? (int)Js.Num(count)!.Value : Math.Max(8, known.Count > 0 ? known.Max() : 8);
        return new PortLayout(Enumerable.Range(1, extent).Where(id => !sfp.Contains(id)).ToList(), sfp);
    }

    /// <summary>profileLayoutNotice(scene, state): '' when the profile fits the connected switch, otherwise the notice.</summary>
    public static string ProfileLayoutNotice(JsonNode? scene, JsonNode? state = null)
    {
        var version = Js.Get(scene, "version") ?? (Js.Has(scene, "zones") ? 2 : 1);
        var current = PortTopology.LightingPorts(state);
        const string invalid = "Review this profile’s saved port layout before applying it.";
        const string legacy = "This older profile uses a different port layout. Review its port assignments before applying it.";
        if (!Js.IsInteger(version) || Js.Num(version) is not (1 or 2 or 3)) return invalid;
        var number = Js.Num(version)!.Value;
        if (number == 3)
        {
            var saved = PortTopology.SceneTopology(scene);
            var topology = Js.Get(scene, "topology");
            var currentModel = Js.Get(Js.Get(state, "topology"), "model") ?? Js.Get(Js.Get(state, "hardware_profile"), "id");
            var model = Js.Get(topology, "model");
            if (saved is null || string.Join(",", Js.Keys(topology).Order(StringComparer.Ordinal)) != "copper_ports,model,sfp_ports,version"
                || model is not null && (Js.Str(model) is not { } modelText || Js.Trim(modelText).Length == 0)
                || saved.Copper.Concat(saved.Sfp).Any(id => id > 64)) return invalid;
            if (!saved.Copper.SequenceEqual(current.Copper) || !saved.Sfp.SequenceEqual(current.Sfp))
                return $"This profile uses {saved.Copper.Count} Ethernet and {saved.Sfp.Count} SFP ports. Choose a profile for this switch’s {current.Copper.Count} Ethernet and {current.Sfp.Count} SFP ports.";
            if (model is not null && currentModel is not null && !Js.StrictEquals(model, currentModel))
                return "This profile was saved for a different switch model. Choose a profile for the connected switch.";
        }
        else
        {
            if (Js.Has(scene, "topology")) return invalid;
            if (number == 2 && (!current.Copper.SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8]) || !current.Sfp.SequenceEqual([9, 10]))) return legacy;
            if (number == 1 && new[] { "zones", "sfp", "sfp_sync" }.Any(key => Js.Has(scene, key))) return invalid;
            if (number == 1 && current.Copper.Count != 8
                && (Js.Keys(Js.Truthy(Js.Get(scene, "overrides")) ? Js.Get(scene, "overrides") : null).Count > 0 || Js.Length(Js.Get(scene, "rules")) > 0)) return legacy;
        }
        return "";
    }

    /// <summary>
    /// profileColors(scene, state, time, animation, date = hostScheduleDate(state)): RGB colors by port for a saved
    /// profile, with its groups, rules, fixed colors and SFP settings over the switch's current facts.
    /// </summary>
    public static Dictionary<int, double[]> ProfileColors(JsonNode? scene, JsonNode? state, double time, Dictionary<string, ProfileAnimation>? animation = null) =>
        ProfileColors(scene, state, time, animation, ScheduleClock.HostDate(state));

    public static Dictionary<int, double[]> ProfileColors(JsonNode? scene, JsonNode? state, double time, Dictionary<string, ProfileAnimation>? animation, DateTime? date)
    {
        animation ??= [];
        var colors = new Dictionary<int, double[]>();
        var master = Js.Get(scene, "brightness") is { } sceneBrightness ? Js.ToNumber(sceneBrightness) : 100;
        var ports = ProfilePorts(scene, state);
        var copper = new Dictionary<double, JsonObject>();
        foreach (var port in Js.Objects(Js.Get(state, "ports"))) if (Js.Num(port["port"]) is { } id) copper[id] = port;
        var optical = new Dictionary<double, JsonObject>();
        foreach (var port in Js.Objects(Js.Get(state, "sfp"))) if (Js.Num(port["port"]) is { } id) optical[id] = port;
        var opticalIds = ports.Sfp.ToHashSet();
        var zoneColors = new Dictionary<double, object?>();
        var composed = new Dictionary<double, (object? Color, double Brightness)>();
        var gamma = Js.Num(Js.Get(Js.Get(state, "output"), "gamma"));
        var rules = Js.Truthy(Js.Get(scene, "rules")) ? Js.Get(scene, "rules") : new JsonArray();
        var hour = date is { } now ? now.Hour + now.Minute / 60.0 + now.Second / 3600.0 : double.NaN;
        foreach (var zone in Js.Get(scene, "zones") is JsonArray zones ? zones : [])
        {
            if (Js.Get(zone, "ports") is not JsonArray zonePorts || !Js.Truthy(Js.Get(Js.Get(zone, "effect"), "id"))) continue;
            var effect = Js.Get(zone, "effect");
            var defaults = EffectRenderer.DefaultConfig(Js.Get(effect, "id"));
            var config = Js.Spread(defaults, effect);
            config["params"] = Js.Spread(defaults["params"], Js.Get(effect, "params"));
            var key = Js.Stringify(new JsonArray(Js.Clone(Js.Get(zone, "id")), Js.Clone(zonePorts), config.DeepClone()));
            if (!animation.TryGetValue(key, out var clock)) animation[key] = clock = new ProfileAnimation();
            var phase = EffectRenderer.PreviewPhase(clock.Motion, config, time);
            var colorPhase = EffectRenderer.PreviewColorPhase(clock.Colors, config, time);
            var resolvedParams = EffectRenderer.ResolvedColorParams(config, time, colorPhase);
            var facts = zonePorts.Select(id => Js.Num(id) is { } n && (opticalIds.Contains((int)n) && n == Math.Floor(n) ? optical : copper).TryGetValue(n, out var port)
                && Js.Truthy(port["facts"]) ? port["facts"] : new JsonObject()).ToList();
            var peakTraffic = EffectRenderer.ActivityPeak(facts, Js.Truthy(config["params"]!["include_uplinks"]));
            for (var index = 0; index < zonePorts.Count; index++)
            {
                if (Js.Num(zonePorts[index]) is not { } id) continue;
                var stateKey = id == Math.Floor(id) ? (int)id : int.MinValue;
                if (!clock.Ports.TryGetValue(stateKey, out var portState)) clock.Ports[stateKey] = portState = new EffectAnimationState();
                zoneColors[id] = Js.StrictEquals(config["id"], "daylight") && !double.IsFinite(hour) ? new double[] { 0, 0, 0 }
                    : EffectRenderer.EffectColor(config, index, zonePorts.Count, time, new EffectFacts(facts[index])
                    {
                        Phase = phase, ColorPhase = colorPhase, ResolvedParams = resolvedParams, PeakTraffic = peakTraffic, AnimationState = portState, Hour = hour,
                    });
            }
        }
        foreach (var id in ports.Copper)
        {
            object? color = zoneColors.TryGetValue(id, out var zoneColor) && zoneColor is not null ? zoneColor : new double[] { 0, 0, 0 };
            var level = master;
            var facts = copper.TryGetValue(id, out var port) && Js.Truthy(port["facts"]) ? port["facts"] : new JsonObject();
            var rule = Rules.MatchingRule(rules, id, facts, date);
            if (rule is not null)
            {
                color = Rules.RenderLook(rule["look"], color, time);
                if (Js.Get(rule["look"], "brightness") is { } ruleLevel) level = Js.ToNumber(ruleLevel);
            }
            var fixedColor = Js.Get(Js.Get(scene, "overrides"), Js.Key(id));
            if (Js.Truthy(fixedColor))
            {
                color = Effects.Paint(Js.Get(fixedColor, "color"));
                if (Js.Get(fixedColor, "brightness") is { } fixedLevel) level = Js.ToNumber(fixedLevel);
            }
            composed[id] = (color, level);
            colors[id] = EffectRenderer.Dim(color, Percent(level));
        }
        var sync = SfpSyncModel.SfpSync(scene);
        var sourcePort = Js.ToNumber(sync["source_port"]);
        foreach (var id in ports.Sfp)
        {
            var member = zoneColors.ContainsKey(id);
            var settings = member ? SfpSyncModel.SfpLight(scene, id) : SfpSyncModel.EffectiveSfpLight(scene, id);
            var telemetry = optical.TryGetValue(id, out var reported) ? reported : new JsonObject();
            var follows = !member && Js.StrictEquals(sync["mode"], "ethernet");
            var hasSource = composed.TryGetValue(sourcePort, out var source);
            var light = Js.Spread(telemetry, settings);
            if (member || follows) light["mode"] = "color";
            else Js.SetOrRemove(light, "mode", Js.Clone(settings["mode"]), settings.ContainsKey("mode"));
            var rule = Rules.MatchingSfpRule(rules, light, date);
            var look = Js.Get(rule, "look");
            var brightness = Js.Get(look, "brightness") is { } ruleLevel ? Js.ToNumber(ruleLevel) : follows ? hasSource ? source.Brightness : master : master;
            if (member || follows || rule is not null && !Js.StrictEquals(Js.Get(look, "style"), "effect"))
            {
                var baseColor = member ? zoneColors[id] : hasSource && SourceTruthy(source.Color) ? source.Color : new double[] { 0, 0, 0 };
                var color = rule is not null ? Rules.RenderLook(look, baseColor, time) : baseColor;
                colors[id] = EffectRenderer.Dim(color, Percent(brightness) * (follows ? 1 : Percent(light["brightness"])));
            }
            else
            {
                var mode = light["mode"];
                colors[id] = Js.StrictEquals(mode, "color")
                    ? EffectGallery.SfpPreviewColor(light, Js.Truthy(light["effect"]) ? Js.ToStr(light["effect"]) : "steady", time, brightness, gamma, SfpSyncModel.SfpNativeAvailable(state))
                    : Js.StrictEquals(mode, "off") ? [0, 0, 0] : PortDisplay.PortDisplayRgb(telemetry, gamma);
            }
        }
        return colors;
    }

    static bool SourceTruthy(object? color) => color switch
    {
        null => false,
        string text => text.Length > 0,
        _ => true,
    };
}
