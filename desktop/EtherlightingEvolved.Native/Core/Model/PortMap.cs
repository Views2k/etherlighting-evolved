// Port of the logic exports of preview/port-map.mjs: port labels, link and PoE readouts, scope options, group names,
// selection and the switch title.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core.Model;

public static class PortMap
{
    /// <summary>LINK_SPEEDS: UniFi speed classes and their labels.</summary>
    public static readonly IReadOnlyList<(int Rate, string Label)> LinkSpeeds =
        [(100, "FE"), (1000, "GbE"), (2500, "2.5 GbE"), (5000, "5 GbE"), (10000, "10 GbE"), (25000, "25 GbE"), (40000, "40 GbE"), (100000, "100 GbE")];

    /// <summary>GROUP_NAME_LIMIT.</summary>
    public const int GroupNameLimit = 40;

    static string Text(JsonNode? value) => Js.Str(value) is { } text ? Js.Trim(text) : "";

    /// <summary>isUplink(port): the link or the facts mark the port as an uplink.</summary>
    public static bool IsUplink(JsonNode? port) =>
        Js.IsTrue(Js.Get(Js.Get(port, "link"), "uplink")) || Js.IsTrue(Js.Get(Js.Get(port, "facts"), "uplink"));

    static JsonObject Label(IReadOnlyList<string> lines) => new() { ["lines"] = Js.StringArray(lines), ["text"] = string.Join(", ", lines) };

    /// <summary>portLabel(port, sfp = false): {lines, text} from the chosen name, uplink detection or client names.</summary>
    public static JsonObject PortLabel(JsonNode? port, bool sfp = false)
    {
        var display = Text(Js.Get(port, "display_name"));
        if (display.Length > 0) return Label([display]);
        if (Js.StrictEquals(Js.Get(port, "name_mode"), "manual")) return Label([$"{(sfp ? "SFP" : "Port")} {Js.ToStr(Js.Get(port, "port"))}"]);
        if (IsUplink(port)) return Label(["Uplink"]);
        var controller = Js.Get(port, "controller");
        var clients = Js.Get(controller, "clients");
        List<string> lines;
        if (clients is JsonArray list && list.Count > 0)
        {
            var assigned = list.Where(client => Js.StrictEquals(Js.Get(client, "name_source"), "assigned") && Text(Js.Get(client, "name")).Length > 0).ToList();
            var names = (assigned.Count > 0 ? assigned : list.ToList()).Select(client => Text(Js.Get(client, "name"))).ToList();
            lines = names.Order(Comparer<string>.Create((first, second) =>
            {
                if (first.Length == 0 || second.Length == 0) return (first.Length == 0 ? 1 : 0) - (second.Length == 0 ? 1 : 0);
                var order = Js.CompareText(first.ToLowerInvariant(), second.ToLowerInvariant());
                return order != 0 ? order : Js.CompareText(first, second);
            })).Select(name => name.Length > 0 ? name : "Unnamed device").ToList();
            var count = Js.Get(controller, "count");
            if (Js.Truthy(Js.Get(controller, "clients_truncated")) && Js.IsInteger(count) && Js.Num(count) > list.Count)
                lines.Add($"+{Js.NumberToString(Js.Num(count)!.Value - list.Count)} more");
        }
        else
        {
            var name = Text(Js.Get(port, "name"));
            if (name.Length == 0) name = Text(Js.Get(port, "label"));
            if (name.Length == 0) name = $"{(sfp ? "SFP" : "Port")} {Js.ToStr(Js.Get(port, "port"))}";
            lines = [name];
        }
        return Label(lines);
    }

    /// <summary>linkStatus(port): {key, label, online}; key is 'disabled', 'offline', 'unknown', 'other' or a speed in Mbps.</summary>
    public static JsonObject LinkStatus(JsonNode? port)
    {
        var link = Js.Get(port, "link");
        if (Js.IsFalse(Js.Get(link, "admin_up"))) return new JsonObject { ["key"] = "disabled", ["label"] = "Disabled", ["online"] = false };
        var up = Js.IsBoolean(link) ? link : Js.Get(link, "link_up") ?? Js.Get(Js.Get(port, "facts"), "link");
        if (Js.IsFalse(up)) return new JsonObject { ["key"] = "offline", ["label"] = "Disconnected", ["online"] = false };
        if (!Js.IsTrue(up)) return new JsonObject { ["key"] = "unknown", ["label"] = "Not reported", ["online"] = null };
        var speedNode = Js.Get(link, "speed_mbps") ?? Js.Get(Js.Get(port, "facts"), "speed");
        var speed = Js.Num(speedNode);
        var known = LinkSpeeds.FirstOrDefault(pair => speed == pair.Rate);
        var isKnown = known.Label is not null;
        JsonNode key = isKnown ? JsonValue.Create(speed!.Value) : speed == 10 ? JsonValue.Create(100) : JsonValue.Create("other");
        var label = isKnown ? known.Label
            : Js.IsFinite(speedNode) && speed > 0 ? speed >= 1000 ? $"{Js.NumberToString(speed!.Value / 1000)} GbE" : $"{Js.NumberToString(speed!.Value)} Mbps"
            : "Connected";
        return new JsonObject { ["key"] = key, ["label"] = label, ["online"] = true };
    }

    /// <summary>portPower(port): {delivering, watts, description} without turning unknown readings into zero.</summary>
    public static JsonObject PortPower(JsonNode? port)
    {
        var poe = Js.Get(port, "poe");
        var delivering = Js.Get(poe, "delivering");
        if (Js.IsFalse(delivering)) return new JsonObject { ["delivering"] = false, ["watts"] = "", ["description"] = "Not supplying power" };
        if (!Js.IsTrue(delivering)) return new JsonObject { ["delivering"] = null, ["watts"] = "", ["description"] = "Not reported" };
        var power = Js.Get(poe, "power_w");
        var watts = Js.IsFinite(power) && Js.Num(power) >= 0 ? $"{Js.ToFixed(Js.Num(power)!.Value, 1)} W" : "";
        return new JsonObject { ["delivering"] = true, ["watts"] = watts, ["description"] = watts.Length > 0 ? watts : "Power usage not reported" };
    }

    /// <summary>portScopeOptions(state): [{value, label, ports, group?}] for All ports, All Ethernet ports, SFP ports and each group.</summary>
    public static JsonArray PortScopeOptions(JsonNode? state)
    {
        var (copper, sfp) = PortTopology.LightingPorts(state);
        var scopes = new JsonArray { new JsonObject { ["value"] = "__allports__", ["label"] = "All ports", ["ports"] = Js.NumberArray(copper.Concat(sfp)) } };
        if (sfp.Count > 0 && copper.Count > 0)
        {
            scopes.Add(new JsonObject { ["value"] = "__all__", ["label"] = "All Ethernet ports", ["ports"] = Js.NumberArray(copper) });
            scopes.Add(new JsonObject { ["value"] = "__sfp__", ["label"] = "SFP ports", ["ports"] = Js.NumberArray(sfp) });
        }
        string[] reserved = ["all ports", "all ethernet ports", "sfp ports", "selected ports"];
        foreach (var zone in Js.Objects(Js.Get(state, "zones")))
        {
            var name = Text(zone["name"]).ToLowerInvariant();
            var ports = Js.Items(zone["ports"]).ToList();
            var samePorts = ports.Count == copper.Count && ports.All(port => Js.Num(port) is { } n && copper.Contains((int)n) && n == Math.Floor(n));
            if (Js.StrictEquals(zone["id"], "all") && name is "all ports" or "all ethernet ports" && samePorts) continue;
            scopes.Add(new JsonObject
            {
                ["value"] = Js.Clone(zone["id"]),
                ["label"] = reserved.Contains(name) ? $"Group: {Js.ToStr(zone["name"])}" : Js.Clone(zone["name"]),
                ["ports"] = Js.Clone(zone["ports"]),
                ["group"] = true,
            });
        }
        return scopes;
    }

    /// <summary>groupNameError(value): '' for a valid group name, otherwise the message.</summary>
    public static string GroupNameError(JsonNode? value)
    {
        var name = Text(value);
        return name.Length == 0 ? "Enter a name for each group." : name.Length > GroupNameLimit ? $"Group names can be up to {GroupNameLimit} characters." : "";
    }

    public static string GroupNameError(string? value) => GroupNameError(value is null ? null : JsonValue.Create(value));

    /// <summary>
    /// selectPort({ports, selected, anchor, port, additive, range}): the next selection and anchor. A range selects
    /// the visible ports between the anchor and the port; additive keeps the current selection.
    /// </summary>
    public static (List<int> Ports, int? Anchor) SelectPort(IReadOnlyList<int> ports, IEnumerable<int> selected, int? anchor, int port, bool additive = false, bool range = false)
    {
        var current = selected.ToList();
        if (!ports.Contains(port)) return (current, anchor);
        if (range && anchor is { } start && ports.Contains(start))
        {
            int first = IndexOf(ports, start), last = IndexOf(ports, port);
            var slice = ports.Skip(Math.Min(first, last)).Take(Math.Abs(last - first) + 1);
            return ((additive ? current.Concat(slice) : slice).Distinct().ToList(), anchor);
        }
        var next = additive ? current.Distinct().ToList() : [];
        if (next.Contains(port)) next.Remove(port);
        else next.Add(port);
        return (next, port);
    }

    static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++) if (list[i] == value) return i;
        return -1;
    }

    /// <summary>selectionHint(platform, multiple = false, touch = false): the selection instructions for the platform.</summary>
    public static string SelectionHint(string? platform, bool multiple = false, bool touch = false)
    {
        if (touch || Regex.IsMatch(platform ?? "", "iphone|ipad|android", RegexOptions.IgnoreCase))
            return multiple ? "Tap ports to add or remove them." : "Tap a port to select it. Use Select multiple to choose several.";
        if (multiple) return "Select ports to add or remove them. Shift-click selects a range.";
        return $"{(Regex.IsMatch(platform ?? "", "mac", RegexOptions.IgnoreCase) ? "Command" : "Ctrl")}-click to add or remove ports. Shift-click selects a range.";
    }

    /// <summary>switchTitle(state): the switch model, the controller's switch name, or "UniFi switch".</summary>
    public static string SwitchTitle(JsonNode? state)
    {
        var model = Text(Js.Get(Js.Get(state, "switch"), "model"));
        if (model.Length > 0) return model;
        var name = Text(Js.Get(Js.Get(state, "controller"), "switch_name"));
        return name.Length > 0 ? name : "UniFi switch";
    }
}
