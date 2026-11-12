using System.Text.Json;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native;

public sealed record ParamInfo(string Key, string Label, string Type, JsonNode? Default, double Min, double Max, double Step,
    string Unit, IReadOnlyList<string> Choices);

public sealed record EffectInfo(string Id, string Name, string Category, string Description, IReadOnlyList<ParamInfo> Params);

public sealed record ZoneInfo(string Id, string Name, IReadOnlyList<int> Ports, string EffectId, JsonObject Params);

public sealed record ManualColor(string Color, int? Brightness);

public sealed record SfpSettings(int Port, string Mode, string Effect, string Color, string SecondaryColor, int Brightness,
    int ColorBrightness, int SecondaryBrightness, double Period, string? EffectZone);

/// <summary>The saved lighting setup the controls edit: groups, manual colors, SFP settings and master brightness.</summary>
public sealed record LightingState(IReadOnlyList<ZoneInfo> Zones, IReadOnlyDictionary<int, ManualColor> Overrides,
    double Brightness, bool Released, IReadOnlyDictionary<int, SfpSettings> Sfp)
{
    public static readonly LightingState Empty = new([], new Dictionary<int, ManualColor>(), 100, false, new Dictionary<int, SfpSettings>());

    public ZoneInfo? ZoneOf(int port) => Zones.FirstOrDefault(zone => zone.Ports.Contains(port));
}

public static class LightingModel
{
    public static IReadOnlyList<EffectInfo> Effects(JsonElement catalog)
    {
        var result = new List<EffectInfo>();
        if (catalog.ValueKind != JsonValueKind.Array) return result;
        foreach (var effect in catalog.EnumerateArray())
        {
            if (WorkspaceModel.Text(effect, "id") is not { } id) continue;
            var parameters = new List<ParamInfo>();
            if (effect.TryGetProperty("params", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var param in list.EnumerateArray())
                {
                    if (WorkspaceModel.Text(param, "key") is not { } key || WorkspaceModel.Text(param, "type") is not { } type) continue;
                    var choices = new List<string>();
                    if (param.TryGetProperty("choices", out var options) && options.ValueKind == JsonValueKind.Array)
                        foreach (var option in options.EnumerateArray())
                            if (option.ValueKind == JsonValueKind.String) choices.Add(option.GetString()!);
                    parameters.Add(new ParamInfo(key, WorkspaceModel.Text(param, "label") ?? key, type,
                        param.TryGetProperty("default", out var fallback) ? JsonNode.Parse(fallback.GetRawText()) : null,
                        WorkspaceModel.Number(param, "min") ?? 0, WorkspaceModel.Number(param, "max") ?? 100,
                        WorkspaceModel.Number(param, "step") ?? (type == "int" ? 1 : 0.1), WorkspaceModel.Text(param, "unit") ?? "", choices));
                }
            result.Add(new EffectInfo(id, WorkspaceModel.Text(effect, "name") ?? id, WorkspaceModel.Text(effect, "category") ?? "",
                WorkspaceModel.Text(effect, "description") ?? "", parameters));
        }
        return result;
    }

    public static LightingState State(JsonElement state, LightingState previous)
    {
        var zones = new List<ZoneInfo>();
        if (state.TryGetProperty("zones", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var zone in list.EnumerateArray())
            {
                if (WorkspaceModel.Text(zone, "id") is not { } id) continue;
                var ports = new List<int>();
                if (zone.TryGetProperty("ports", out var members) && members.ValueKind == JsonValueKind.Array)
                    foreach (var member in members.EnumerateArray())
                        if (member.ValueKind == JsonValueKind.Number && member.TryGetInt32(out var port)) ports.Add(port);
                var parameters = zone.TryGetProperty("effect", out var effect) && effect.ValueKind == JsonValueKind.Object
                                 && effect.TryGetProperty("params", out var values) && values.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(values.GetRawText()) as JsonObject ?? new JsonObject()
                    : new JsonObject();
                zones.Add(new ZoneInfo(id, WorkspaceModel.Text(zone, "name") ?? id, ports,
                    WorkspaceModel.Text(zone, "effect", "id") ?? "static", parameters));
            }
        var overrides = new Dictionary<int, ManualColor>();
        if (state.TryGetProperty("overrides", out var manual) && manual.ValueKind == JsonValueKind.Object)
            foreach (var item in manual.EnumerateObject())
                if (int.TryParse(item.Name, out var port) && WorkspaceModel.Text(item.Value, "color") is { } color)
                    overrides[port] = new ManualColor(color, WorkspaceModel.Number(item.Value, "brightness") is { } level ? (int)level : null);
        var sfp = state.TryGetProperty("sfp", out var lights) ? Sfp(lights) : previous.Sfp;
        return new LightingState(zones, overrides, WorkspaceModel.Number(state, "brightness") ?? previous.Brightness,
            WorkspaceModel.Text(state, "mode") == "released", sfp);
    }

    public static IReadOnlyDictionary<int, SfpSettings> Sfp(JsonElement lights)
    {
        var result = new Dictionary<int, SfpSettings>();
        if (lights.ValueKind != JsonValueKind.Array) return result;
        foreach (var light in lights.EnumerateArray())
        {
            if (WorkspaceModel.Number(light, "port") is not { } number) continue;
            var port = (int)number;
            result[port] = new SfpSettings(port, WorkspaceModel.Text(light, "mode") ?? "unmanaged",
                WorkspaceModel.Text(light, "effect") ?? "steady", WorkspaceModel.Text(light, "color") ?? "#ffffff",
                WorkspaceModel.Text(light, "secondary_color") ?? "#ffffff", (int)(WorkspaceModel.Number(light, "brightness") ?? 100),
                (int)(WorkspaceModel.Number(light, "color_brightness") ?? 100), (int)(WorkspaceModel.Number(light, "secondary_brightness") ?? 100),
                WorkspaceModel.Number(light, "period") ?? 4, WorkspaceModel.Text(light, "effect_zone"));
        }
        return result;
    }

    /// <summary>"Ports 1–4, 7" for a set of port numbers.</summary>
    public static string Ranges(IEnumerable<int> ports)
    {
        var ids = ports.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            var first = ids[i];
            while (i + 1 < ids.Count && ids[i + 1] == ids[i] + 1) i++;
            parts.Add(ids[i] == first ? $"{first}" : ids[i] == first + 1 ? $"{first}, {ids[i]}" : $"{first}–{ids[i]}");
        }
        return string.Join(", ", parts);
    }

    /// <summary>The group list after giving the selected ports their own group with the chosen effect.</summary>
    public static (JsonArray Zones, string Id) Regroup(LightingState state, IReadOnlyCollection<int> selected, string effectId, JsonObject parameters)
    {
        var chosen = selected.ToHashSet();
        var zones = new JsonArray();
        var used = new HashSet<string>();
        foreach (var zone in state.Zones)
        {
            var rest = zone.Ports.Where(port => !chosen.Contains(port)).ToList();
            if (rest.Count == 0) continue;
            used.Add(zone.Id);
            zones.Add(new JsonObject
            {
                ["id"] = zone.Id, ["name"] = zone.Name, ["ports"] = new JsonArray(rest.Select(port => (JsonNode)port).ToArray()),
                ["effect"] = new JsonObject { ["id"] = zone.EffectId, ["params"] = zone.Params.DeepClone() },
            });
        }
        var ordered = chosen.Order().ToList();
        var baseId = "selection-" + string.Join("-", ordered);
        var id = baseId;
        for (var suffix = 2; used.Contains(id); suffix++) id = $"{baseId}-{suffix}";
        var name = $"Ports {Ranges(ordered)}";
        if (name.Length > 40) name = name[..39] + "…";
        zones.Add(new JsonObject
        {
            ["id"] = id, ["name"] = name, ["ports"] = new JsonArray(ordered.Select(port => (JsonNode)port).ToArray()),
            ["effect"] = new JsonObject { ["id"] = effectId, ["params"] = parameters.DeepClone() },
        });
        return (zones, id);
    }
}
