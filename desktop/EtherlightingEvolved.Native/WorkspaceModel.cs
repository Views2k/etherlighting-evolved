using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native;

/// <summary>What one port tile shows. Records compare by value, so an unchanged port is skipped cheaply.</summary>
public sealed record PortView(int Port, bool Sfp, byte R, byte G, byte B, string Label, string Link, bool Online,
    bool Uplink, string Power);

public sealed record WorkspaceSnapshot(string Title, string Status, bool Live, IReadOnlyList<PortView> Ports);

/// <summary>Reads the EE workspace payloads. Mirrors the browser workspace's port-display and port-map rules.</summary>
public static partial class WorkspaceModel
{
    static readonly (double Speed, string Label)[] Speeds =
    [
        (100, "FE"), (1000, "GbE"), (2500, "2.5 GbE"), (5000, "5 GbE"), (10000, "10 GbE"),
        (25000, "25 GbE"), (40000, "40 GbE"), (100000, "100 GbE"),
    ];

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    public static List<PortView> Ports(JsonElement copper, JsonElement sfp, double gamma)
    {
        var result = new List<PortView>();
        if (copper.ValueKind == JsonValueKind.Array)
            foreach (var port in copper.EnumerateArray())
                if (Parse(port, false, gamma) is { } view) result.Add(view);
        var copperIds = result.Select(port => port.Port).ToHashSet();
        if (sfp.ValueKind == JsonValueKind.Array)
            foreach (var port in sfp.EnumerateArray())
                if (Parse(port, true, gamma) is { } view && !copperIds.Contains(view.Port)) result.Add(view);
        return result;
    }

    public static PortView? Parse(JsonElement port, bool sfp, double gamma)
    {
        if (Number(port, "port") is not { } id || id != Math.Floor(id) || id < 1 || id > 256) return null;
        var number = (int)id;
        var (r, g, b) = DisplayColor(port, gamma);
        var uplink = Bool(port, "link", "uplink") == true || Bool(port, "facts", "uplink") == true;
        var (link, online) = LinkStatus(port);
        return new PortView(number, sfp, r, g, b, Label(port, sfp, number, uplink), link, online, uplink, Power(port));
    }

    public static (byte R, byte G, byte B) DisplayColor(JsonElement port, double gamma)
    {
        if (port.ValueKind != JsonValueKind.Object) return (0, 0, 0);
        if (port.TryGetProperty("display_rgb", out var shown))
        {
            if (shown.ValueKind != JsonValueKind.Array || shown.GetArrayLength() != 3) return (0, 0, 0);
            Span<byte> channels = stackalloc byte[3];
            var index = 0;
            foreach (var channel in shown.EnumerateArray())
            {
                if (channel.ValueKind != JsonValueKind.Number) return (0, 0, 0);
                channels[index++] = Channel(channel.GetDouble());
            }
            return (channels[0], channels[1], channels[2]);
        }
        var mode = Text(port, "mode");
        var ruled = port.TryGetProperty("rule", out var rule)
                    && rule.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined);
        if (mode == "off" || mode == "unmanaged" && !ruled) return (0, 0, 0);
        var preview = Text(port, "preview");
        var encoded = preview ?? Text(port, "output", "color");
        if (encoded is null || !HexColor().IsMatch(encoded)) return (0, 0, 0);
        var level = preview is null ? Math.Clamp(Number(port, "output", "level") ?? 100, 0, 100) / 100 : 1;
        var curve = double.IsFinite(gamma) && gamma > 0 ? gamma : 2.2;
        byte Map(int offset) =>
            Channel(255 * Math.Pow(int.Parse(encoded.AsSpan(offset, 2), NumberStyles.HexNumber) / 255.0 * level, 1 / curve));
        return (Map(1), Map(3), Map(5));
    }

    static (string, bool) LinkStatus(JsonElement port)
    {
        var link = port.TryGetProperty("link", out var value) ? value : default;
        if (Bool(link, "admin_up") == false) return ("Disabled", false);
        bool? up = link.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => Bool(link, "link_up"),
        };
        up ??= Bool(port, "facts", "link");
        if (up == false) return ("Disconnected", false);
        if (up != true) return ("Not reported", false);
        var speed = Number(link, "speed_mbps") ?? Number(port, "facts", "speed");
        if (speed is not { } rate || !double.IsFinite(rate) || rate <= 0) return ("Connected", true);
        foreach (var (known, label) in Speeds)
            if (known == rate) return (label, true);
        return (rate >= 1000
            ? $"{(rate / 1000).ToString(CultureInfo.InvariantCulture)} GbE"
            : $"{rate.ToString(CultureInfo.InvariantCulture)} Mbps", true);
    }

    static string Label(JsonElement port, bool sfp, int number, bool uplink)
    {
        if (uplink) return "Uplink";
        if (port.TryGetProperty("controller", out var controller) && controller.ValueKind == JsonValueKind.Object
            && controller.TryGetProperty("clients", out var clients) && clients.ValueKind == JsonValueKind.Array
            && clients.GetArrayLength() > 0)
        {
            var all = new List<(string Name, bool Assigned)>();
            foreach (var client in clients.EnumerateArray())
            {
                var name = Text(client, "name")?.Trim() ?? "";
                all.Add((name, name.Length > 0 && Text(client, "name_source") == "assigned"));
            }
            var names = (all.Any(item => item.Assigned) ? all.Where(item => item.Assigned) : all).Select(item => item.Name).ToList();
            names.Sort((a, b) => a.Length == 0 || b.Length == 0
                ? (a.Length == 0 ? 1 : 0) - (b.Length == 0 ? 1 : 0)
                : string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
            var count = Math.Max(names.Count, (int)(Number(controller, "count") ?? 0));
            var first = names[0].Length == 0 ? "Unnamed device" : names[0];
            return count > 1 ? $"{first} +{count - 1}" : first;
        }
        foreach (var key in new[] { "name", "label" })
            if (Text(port, key)?.Trim() is { Length: > 0 } text) return text;
        return $"{(sfp ? "SFP" : "Port")} {number}";
    }

    static string Power(JsonElement port)
    {
        if (Bool(port, "poe", "delivering") != true) return "";
        return Number(port, "poe", "power_w") is { } watts && double.IsFinite(watts) && watts >= 0
            ? $"{watts.ToString("0.0", CultureInfo.InvariantCulture)} W"
            : "";
    }

    public static string Title(JsonElement state) =>
        Text(state, "switch", "model") ?? Text(state, "switch", "hardware_profile", "model")
        ?? Text(state, "controller", "switch_name") ?? "UniFi switch";

    public static double Gamma(JsonElement state) => Number(state, "output", "gamma") is { } gamma && gamma > 0 ? gamma : 2.2;

    static byte Channel(double value) => double.IsFinite(value) ? (byte)Math.Clamp(Math.Round(value), 0, 255) : (byte)0;

    static JsonElement? Find(JsonElement element, ReadOnlySpan<string> path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }
        return element;
    }

    public static string? Text(JsonElement element, params string[] path) =>
        Find(element, path) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text ? text : null;

    public static double? Number(JsonElement element, params string[] path) =>
        Find(element, path) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    public static bool? Bool(JsonElement element, params string[] path) => Find(element, path)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
