using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;

namespace EtherlightingEvolved.Native.Pages.Settings;

public static class SwitchHealth
{
    public static (string Status, List<(string Label, string Value)> Rows) Read(JsonNode? state, bool unavailable = false)
    {
        var report = SwitchHealthModel.SwitchHealth(state, unavailable);
        var rows = report.Arr("rows").OfType<JsonArray>()
            .Select(row => (row[0]!.GetValue<string>(), row[1]!.GetValue<string>()))
            .ToList();
        return (report["status"]!.GetValue<string>(), rows);
    }
}
