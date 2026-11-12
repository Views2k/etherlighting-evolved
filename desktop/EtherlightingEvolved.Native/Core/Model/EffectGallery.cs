// Port of the logic exports of preview/effect-gallery.mjs: the configuration and status a gallery card shows, the SFP
// effect list and the SFP indicator preview color. The gallery's DOM rendering (renderEffectGallery,
// orderGalleryCards, fitGalleryPreview) belongs to the Effects page.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class EffectGallery
{
    /// <summary>SFP_EFFECTS: the effects an SFP indicator plays.</summary>
    public static readonly JsonArray SfpEffects = (JsonArray)JsonNode.Parse("""
        [{"id":"steady","name":"Steady","description":"Keep a constant color.","category":"Ambient"},
         {"id":"pulse","name":"Gentle pulse","description":"Fade the primary color in and out.","category":"Ambient"},
         {"id":"two_color","name":"Two-color fade","description":"Blend smoothly between your two colors.","category":"Ambient"},
         {"id":"color_cycle","name":"Color cycle","description":"Move smoothly through the color wheel.","category":"Motion"}]
        """)!;

    static double Percent(JsonNode? value)
    {
        var number = Js.ToNumber(value);
        return Math.Max(0, Math.Min(100, double.IsFinite(number) ? number : 100)) / 100;
    }

    static double Percent(double value) => Math.Max(0, Math.Min(100, double.IsFinite(value) ? value : 100)) / 100;

    static string Signature(JsonNode? config)
    {
        var entries = Js.Entries(Js.Truthy(Js.Get(config, "params")) ? Js.Get(config, "params") : null)
            .OrderBy(entry => entry.Key, Comparer<string>.Create(Js.CompareText))
            .Select(entry => (JsonNode?)new JsonArray(entry.Key, Js.Clone(entry.Value)));
        return Js.Stringify(new JsonArray(Js.Clone(Js.Get(config, "id")), Js.ArrayOf(entries)));
    }

    static List<int> SelectedSfp(JsonNode? state, IEnumerable<int> selected)
    {
        var sfp = PortTopology.LightingPorts(state).Sfp;
        return selected.Where(sfp.Contains).ToList();
    }

    static List<int> SelectedCopper(JsonNode? state, IEnumerable<int> selected)
    {
        var copper = PortTopology.LightingPorts(state).Copper;
        return selected.Where(copper.Contains).ToList();
    }

    static JsonObject Defaults(JsonNode? effect)
    {
        var defaults = new JsonObject();
        foreach (var param in Js.Objects(Js.Get(effect, "params")))
            if (param.TryGetPropertyValue("default", out var value)) defaults[Js.ToStr(param["key"])] = Js.Clone(value);
        return defaults;
    }

    /// <summary>
    /// galleryConfig(effect, state, selectedPorts, remembered, draft): the settings a card previews: a staged draft for
    /// the selection, the selection's applied effect, the remembered settings, or another group's settings.
    /// </summary>
    public static JsonObject GalleryConfig(JsonNode? effect, JsonNode? state, IEnumerable<int>? selectedPorts = null,
        IReadOnlyDictionary<string, JsonObject>? remembered = null, JsonNode? draft = null) =>
        GalleryConfig(effect, state, selectedPorts, id => remembered is not null && remembered.TryGetValue(id, out var value) ? value : null, draft);

    public static JsonObject GalleryConfig(JsonNode? effect, JsonNode? state, IEnumerable<int>? selectedPorts, JsonNode? remembered, JsonNode? draft = null) =>
        GalleryConfig(effect, state, selectedPorts, id => Js.Get(remembered, id), draft);

    static JsonObject GalleryConfig(JsonNode? effect, JsonNode? state, IEnumerable<int>? selectedPorts, Func<string, JsonNode?> remembered, JsonNode? draft)
    {
        var defaults = Defaults(effect);
        var selected = (selectedPorts ?? []).ToHashSet();
        var zones = Js.Objects(Js.Truthy(Js.Get(state, "zones")) ? Js.Get(state, "zones") : null).ToList();
        var id = Js.Get(effect, "id");
        bool Selected(JsonNode? port) => Js.Num(port) is { } n && n == Math.Floor(n) && selected.Contains((int)n);
        var activeDraft = EffectDraft.EffectDrafts(draft).OfType<JsonObject>()
            .LastOrDefault(item => Js.StrictEquals(item["id"], id) && Js.Items(item["ports"]).Any(Selected));
        var current = zones.FirstOrDefault(zone => Js.StrictEquals(Js.Get(zone["effect"], "id"), id) && Js.Items(zone["ports"]).Any(Selected));
        JsonNode? chosen = activeDraft;
        if (!Js.Truthy(chosen)) chosen = current?["effect"];
        if (!Js.Truthy(chosen)) chosen = remembered(Js.ToStr(id));
        if (!Js.Truthy(chosen)) chosen = zones.FirstOrDefault(zone => Js.StrictEquals(Js.Get(zone["effect"], "id"), id))?["effect"];
        var parameters = Js.Spread(defaults, Js.Truthy(Js.Get(chosen, "params")) ? Js.Get(chosen, "params") : null);
        return new JsonObject { ["id"] = Js.Clone(id), ["params"] = parameters };
    }

    static (string Kind, JsonObject? Effect) EffectOwnership(JsonNode? state, int id, JsonNode? draft)
    {
        var optical = Js.Objects(Js.Get(state, "sfp")).FirstOrDefault(light => Js.StrictEquals(light["port"], id));
        var port = optical ?? Js.Objects(Js.Get(state, "ports")).FirstOrDefault(item => Js.StrictEquals(item["port"], id));
        var pending = EffectDraft.EffectDraftForPort(draft, id);
        var effect = EffectDraft.PortEffect(state, draft, id);
        if (Js.Truthy(Js.Get(Js.Get(draft, "colors"), Js.Key(id)))) return ("manual-preview", effect);
        if ((Js.Truthy(port?["override"]) || Js.Truthy(Js.Get(Js.Get(state, "overrides"), Js.Key(id))) || Js.StrictEquals(Js.Get(port?["source"], "kind"), "override"))
            && !Js.Includes(Js.Get(draft, "returnToEffect"), id) && !Js.Truthy(pending?["clearOverrides"])) return ("manual", effect);
        if (pending is not null) return ("preview", effect);
        if (Js.Truthy(optical?["rule"]) || Js.StrictEquals(Js.Get(port?["source"], "kind"), "rule")) return ("rule", effect);
        return ("effect", effect);
    }

    static JsonObject Status(string label, bool selected) => new() { ["label"] = label, ["selected"] = selected };

    /// <summary>galleryStatus(effect, state, selected, draft, config): {label, selected} for an effect card.</summary>
    public static JsonObject GalleryStatus(JsonNode? effect, JsonNode? state = null, IEnumerable<int>? selected = null, JsonNode? draft = null, JsonNode? config = null)
    {
        var ports = (selected ?? []).ToList();
        var owners = SelectedCopper(state, ports).Concat(SelectedSfp(state, ports)).Select(id => EffectOwnership(state, id, draft)).ToList();
        var id = Js.Get(effect, "id");
        var assigned = owners.Where(owner => Js.StrictEquals(Js.Get(owner.Effect, "id"), id)).ToList();
        if (assigned.Count == 0) return Status("", false);
        var previewing = assigned.Count(owner => owner.Kind == "preview");
        if (previewing > 0)
            return Status(previewing == owners.Count ? "Previewing" : $"Previewing on {previewing} {(previewing == 1 ? "port" : "ports")}", true);
        var applied = assigned.Where(owner => owner.Kind == "effect").ToList();
        if (applied.Count == owners.Count)
        {
            var defaults = Defaults(effect);
            var configs = applied.Select(owner =>
            {
                var merged = Js.Spread(owner.Effect);
                merged["params"] = Js.Spread(defaults, owner.Effect?["params"]);
                return Signature(merged);
            }).ToHashSet();
            if (configs.Count > 1) return Status("Mixed settings", false);
            var mode = Js.Get(state, "mode");
            return Status(Js.Truthy(config) && !configs.Contains(Signature(config)) ? "Applied with other settings"
                : Js.Truthy(mode) && !Js.StrictEquals(mode, "running") ? "Saved effect" : "Applied", true);
        }
        if (applied.Count > 0) return Status($"Applied to {applied.Count} of {owners.Count}", false);
        var kinds = assigned.Select(owner => owner.Kind).ToHashSet();
        var label = kinds.Count > 1 ? "Mixed output" : kinds.Contains("manual-preview") ? "Fixed color preview" : kinds.Contains("manual") ? "Fixed color active"
            : kinds.Contains("rule") ? "Automation active" : "";
        return Status(label, false);
    }

    /// <summary>
    /// sfpPreviewColor(settings, effect, time, master = 100, gamma = 2.2, native = true): the SFP indicator color. Native
    /// periods use the switch's 16-bit anchor interpolation; other periods use a cosine fade.
    /// </summary>
    public static double[] SfpPreviewColor(JsonNode? settings, string? effect, double time, double master = 100, double? gamma = 2.2, bool native = true)
    {
        var light = Js.Spread(new JsonObject
        {
            ["color"] = "#ffffff", ["secondary_color"] = "#006fff", ["color_brightness"] = 100, ["secondary_brightness"] = 100, ["brightness"] = 100, ["period"] = 6,
        }, settings);
        var color = light["color"];
        if (native && SfpSyncModel.SfpNativePeriod(effect, light["period"]))
        {
            var primary = EffectRenderer.Dim(color, Percent(light["color_brightness"]));
            double[][] anchors;
            if (effect == "pulse") anchors = [primary, [0, 0, 0]];
            else if (effect == "two_color") anchors = [primary, EffectRenderer.Dim(light["secondary_color"], Percent(light["secondary_brightness"]))];
            else
            {
                var hsv = ColorMath.HexToHsv(Js.ToStr(color));
                anchors = Enumerable.Range(0, 6)
                    .Select(index => EffectRenderer.Dim(EffectRenderer.HsvToRgb((hsv[0] + index * 60) % 360, Math.Max(.75, hsv[1]), hsv[2]), Percent(light["color_brightness"])))
                    .ToArray();
            }
            var period = Js.ToNumber(light["period"]);
            var position = (time / period % 1 + 1) % 1 * anchors.Length;
            var index = Math.Floor(position);
            var fraction = position - index;
            if (double.IsNaN(index)) return [double.NaN, double.NaN, double.NaN];
            var curve = gamma is { } g && double.IsFinite(g) && g > 0 ? g : 2.2;
            var scale = Percent(master) * Percent(light["brightness"]);
            double Encode(double channel) => Js.Round(65280 * Math.Pow(channel / 255 * scale, curve));
            var start = anchors[(int)index];
            var end = anchors[((int)index + 1) % anchors.Length];
            return start.Select((channel, c) => 255 * Math.Pow((Encode(channel) * (1 - fraction) + Encode(end[c]) * fraction) / 65280, 1 / curve)).ToArray();
        }
        var cycle = Js.ToNumber(light["period"]);
        var phase = time / Math.Max(2, Js.Truthy(cycle) ? cycle : 6) % 1;
        var weight = (1 - Math.Cos(Math.PI * 2 * phase)) / 2;
        var shown = EffectRenderer.Dim(color, Percent(light["color_brightness"]));
        if (effect == "pulse") shown = EffectRenderer.Dim(shown, 1 - weight);
        else if (effect == "two_color") shown = EffectRenderer.Mix(shown, EffectRenderer.Dim(light["secondary_color"], Percent(light["secondary_brightness"])), weight);
        else if (effect == "color_cycle")
        {
            var hsv = ColorMath.HexToHsv(Js.ToStr(color));
            var sector = (hsv[0] / 360 + phase) % 1 * 6;
            var sectorFraction = sector % 1;
            shown = EffectRenderer.Dim(EffectRenderer.HsvToRgb((Math.Floor(sector) + sectorFraction * sectorFraction * (3 - 2 * sectorFraction)) * 60, Math.Max(.75, hsv[1]), hsv[2]),
                Percent(light["color_brightness"]));
        }
        return EffectRenderer.Dim(shown, Percent(master) * Percent(light["brightness"]));
    }

    /// <summary>sfpGalleryStatus(effect, state, selected, draft): {label, selected} for an SFP effect card.</summary>
    public static JsonObject SfpGalleryStatus(JsonNode? effect, JsonNode? state = null, IEnumerable<int>? selected = null, JsonNode? draft = null)
    {
        var sync = SfpSyncModel.SfpSync(state, draft);
        if (Js.StrictEquals(sync["mode"], "ethernet")) return Status("", false);
        var ports = SelectedSfp(state, selected ?? []);
        var lights = ports.Select(port => SfpSyncModel.EffectiveSfpLight(state, port, draft)).ToList();
        var id = Js.Get(effect, "id");
        bool Matches(JsonObject light) => EffectDraft.PortEffect(state, draft, Js.ToNumber(light["port"])) is null
                                          && Js.StrictEquals(light["mode"], "color") && Js.StrictEquals(light["effect"], id);
        var matching = lights.Where(Matches).ToList();
        if (matching.Count == 0) return Status("", false);
        var changed = SfpSyncModel.SfpDraftPorts(state, draft).ToHashSet();
        var staged = ports.Where((port, index) => changed.Contains(port) && Matches(lights[index])).ToList();
        if (staged.Count > 0)
            return Status(staged.Count == ports.Count ? "Previewing" : $"Previewing on {staged.Count} {(staged.Count == 1 ? "port" : "ports")}", true);
        var active = matching.Where(light => !Js.Truthy(light["rule"])).ToList();
        if (active.Count == 0) return Status("Automation active", false);
        if (active.Count != ports.Count) return Status($"Applied to {active.Count} of {ports.Count}", false);
        var settings = active.Select(light => Js.Stringify(new JsonArray(Js.Clone(light["color"]), Js.Clone(light["secondary_color"]), Js.Clone(light["brightness"]),
            Js.Clone(light["color_brightness"]), Js.Clone(light["secondary_brightness"]), Js.Clone(light["period"])))).ToHashSet();
        if (settings.Count > 1) return Status("Mixed settings", false);
        var mode = Js.Get(state, "mode");
        return Status(Js.Truthy(mode) && !Js.StrictEquals(mode, "running") ? "Saved effect" : "Applied", true);
    }
}
