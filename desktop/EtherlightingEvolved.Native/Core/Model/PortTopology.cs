// Port of preview/port-topology.mjs: which reported port IDs carry lighting, saved profile layouts and range text.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

/// <summary>The {copper, sfp} lists returned by lightingPorts, sceneTopology and profilePorts.</summary>
public sealed record PortLayout(List<int> Copper, List<int> Sfp)
{
    public static PortLayout Empty => new([], []);

    public JsonObject ToJson() => new() { ["copper"] = Js.NumberArray(Copper), ["sfp"] = Js.NumberArray(Sfp) };

    public bool Same(PortLayout other) => Copper.SequenceEqual(other.Copper) && Sfp.SequenceEqual(other.Sfp);
}

public static class PortTopology
{
    /// <summary>validPort(id): an integer from 1 to 256.</summary>
    public static bool ValidPort(JsonNode? id) => Js.Num(id) is { } number && ValidPort(number);

    public static bool ValidPort(double id) => Js.IsInteger(id) && id > 0 && id <= 256;

    /// <summary>portIds(values): the valid port IDs, without duplicates, in ascending order.</summary>
    public static List<int> PortIds(JsonNode? values) => PortIds(Js.Items(values));

    public static List<int> PortIds(JsonArray values) => PortIds((IEnumerable<JsonNode?>)values);

    public static List<int> PortIds(IEnumerable<JsonNode?> values) =>
        PortIds(values.Select(Js.Num).Where(number => number is not null).Select(number => number!.Value));

    public static List<int> PortIds(IEnumerable<double> values) =>
        values.Where(ValidPort).Select(value => (int)value).Distinct().Order().ToList();

    public static List<int> PortIds(IEnumerable<int> values) => PortIds(values.Select(value => (double)value));

    /// <summary>lightingPorts(state): the reported copper and SFP lighting ports. SFP IDs that repeat copper IDs are dropped.</summary>
    public static PortLayout LightingPorts(JsonNode? state)
    {
        var count = Js.Get(state, "num_ports") ?? Js.Get(Js.Get(state, "switch"), "num_ports");
        var ports = Js.Get(state, "ports");
        List<int> copper = ports is JsonArray copperList ? PortIds(copperList.Select(port => Js.Get(port, "port")))
            : Js.IsInteger(count) && Js.Num(count) is >= 0 and <= 256 ? Enumerable.Range(1, (int)Js.Num(count)!.Value).ToList() : [];
        var sfpNode = Js.Get(state, "sfp");
        var sfp = sfpNode is JsonArray sfpList ? PortIds(sfpList.Select(port => Js.Get(port, "port")))
            : PortIds(Js.Get(state, "sfp_ports") ?? Js.Get(Js.Get(state, "switch"), "sfp_ports"));
        return new PortLayout(copper, sfp.Where(id => !copper.Contains(id)).ToList());
    }

    /// <summary>previewGrid(count): rows of at most 12 with the columns balanced.</summary>
    public static (int Rows, int Columns) PreviewGrid(int count)
    {
        var rows = (int)Math.Ceiling(count / 12.0);
        return (rows, rows != 0 ? (int)Math.Ceiling(count / (double)rows) : 0);
    }

    /// <summary>portRanges(values): "1–3, 5, 8–10"; two adjacent ports are listed as "9, 10".</summary>
    public static string PortRanges(JsonNode? values) => PortRanges(PortIds(values));

    public static string PortRanges(IEnumerable<int> values)
    {
        var ids = PortIds(values);
        var ranges = new List<string>();
        for (var index = 0; index < ids.Count; index++)
        {
            var first = ids[index];
            while (index + 1 < ids.Count && ids[index + 1] == ids[index] + 1) index++;
            var last = ids[index];
            ranges.Add(last - first > 1 ? $"{first}–{last}" : last == first ? first.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{first}, {last}");
        }
        return string.Join(", ", ranges);
    }

    /// <summary>sceneTopology(scene): the saved version 1 topology, or null when it is missing or invalid.</summary>
    public static PortLayout? SceneTopology(JsonNode? scene)
    {
        var topology = Js.Get(scene, "topology");
        if (!Js.StrictEquals(Js.Get(topology, "version"), 1) || Js.Get(topology, "copper_ports") is not JsonArray copperPorts
            || Js.Get(topology, "sfp_ports") is not JsonArray sfpPorts) return null;
        var copper = PortIds(copperPorts);
        var sfp = PortIds(sfpPorts);
        if (copper.Count != copperPorts.Count || sfp.Count != sfpPorts.Count || sfp.Any(copper.Contains)) return null;
        return new PortLayout(copper, sfp);
    }
}
