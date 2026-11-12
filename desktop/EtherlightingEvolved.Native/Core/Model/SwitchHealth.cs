// Port of preview/switch-health.mjs: the switch readings shown in Settings. The class is named SwitchHealthModel
// because the module exports a function named switchHealth.
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class SwitchHealthModel
{
    static bool Finite(JsonNode? value) => Js.Num(value) is { } number && double.IsFinite(number) && number >= 0;

    static string Number(JsonNode? value, int digits = 1) => Finite(value) ? Js.LocaleNumber(Js.Num(value)!.Value, digits) : "—";

    static string Number(double value, int digits = 1) => double.IsFinite(value) && value >= 0 ? Js.LocaleNumber(value, digits) : "—";

    static string Bytes(JsonNode? value)
    {
        if (!Finite(value)) return "—";
        var bytes = Js.Num(value)!.Value;
        var gib = Math.Pow(1024, 3);
        var scale = bytes >= gib ? gib : Math.Pow(1024, 2);
        var unit = scale == gib ? "GiB" : "MiB";
        return $"{Number(bytes / scale)} {unit}";
    }

    static string Usage(JsonNode? value)
    {
        var used = Js.Get(value, "used_bytes");
        var total = Js.Get(value, "total_bytes");
        return Js.Truthy(value) && Finite(used) && Finite(total) && Js.Num(total) > 0 && Js.Num(used) <= Js.Num(total)
            ? $"{Bytes(used)} / {Bytes(total)} · {Number(Js.Num(used)!.Value / Js.Num(total)!.Value * 100, 0)}%"
            : "Not reported";
    }

    /// <summary>switchHealth(state, unavailable): {status, rows: [[label, value], ...]}.</summary>
    public static JsonObject SwitchHealth(JsonNode? state = null, bool unavailable = false)
    {
        var stats = Js.Get(state, "stats");
        var idle = Js.Get(stats, "switch_cpu_idle");
        var hasCpuSample = Finite(Js.Get(stats, "cpu_sample_age_ms"));
        var resources = Js.Get(stats, "switch_health");
        var resourceRows = new List<(string, string)>();
        var temperatures = Js.Get(resources, "temperatures") is JsonArray sensors ? sensors.Where(sensor => Js.IsFinite(Js.Get(sensor, "celsius"))).ToList() : [];
        if (temperatures.Count > 0)
            foreach (var sensor in temperatures)
                resourceRows.Add(($"Temperature · {(Js.Truthy(Js.Get(sensor, "label")) ? Js.ToStr(Js.Get(sensor, "label")) : "Switch")}",
                    $"{Js.LocaleNumber(Js.Num(Js.Get(sensor, "celsius"))!.Value, 1)} °C"));
        else resourceRows.Add(("Temperature", "Not reported"));
        resourceRows.Add(("Memory used", Usage(Js.Get(resources, "memory"))));
        var storage = Js.Get(resources, "storage") is JsonArray drives ? drives.ToList() : [];
        if (storage.Count > 0)
            foreach (var drive in storage) resourceRows.Add(($"Storage · {Js.ToStr(Js.Get(drive, "mount"))}", Usage(drive)));
        else resourceRows.Add(("Storage used", "Not reported"));
        var healthAge = Js.Get(stats, "health_sample_age_ms");
        resourceRows.Add(("Resource sample age", Finite(healthAge) ? $"{Number(Js.Num(healthAge)!.Value / 1000)} s" : "—"));
        var poe = Js.Get(state, "poe_summary");
        var poeRows = new List<(string, string)>();
        if (Js.Truthy(poe))
        {
            var status = Js.Get(poe, "status");
            var fresh = !unavailable && Js.Truthy(Js.Get(state, "connected")) && Js.StrictEquals(status, "ready");
            var draw = Js.Get(poe, "draw_w");
            poeRows.Add(("PoE draw", Finite(draw) ? Number(draw) + " W" + (fresh ? "" : " · " + (Js.StrictEquals(status, "partial") ? "Partial sample" : "Last sample")) : "Waiting for a sample"));
            var budget = Js.Get(poe, "budget_w");
            poeRows.Add(("PoE budget", Finite(budget) ? Number(budget) + " W" : "Not reported"));
            var headroom = Js.Get(poe, "headroom_w");
            poeRows.Add(("PoE headroom", fresh && Js.IsFinite(headroom)
                ? Js.Num(headroom) < 0 ? Number(-Js.Num(headroom)!.Value) + " W over budget" : Number(headroom) + " W"
                : "Waiting for a complete sample"));
            foreach (var consumer in Js.Items(Js.Get(poe, "consumers")).Take(5))
                poeRows.Add(("PoE · Port " + Js.ToStr(Js.Get(consumer, "port")), Number(Js.Get(consumer, "power_w")) + " W"));
        }
        var statusText = Js.Truthy(Js.Get(state, "reconnect_required")) ? "Reconnect the switch to resume lighting."
            : unavailable || !Js.Truthy(Js.Get(state, "connected")) ? "Last reported readings · switch disconnected"
            : Js.Truthy(Js.Get(stats, "health_error")) ? "Latest resource update unavailable · showing last readings" : "Switch readings";
        var ack = Js.Get(stats, "ack_p95_ms");
        var cpuAge = Js.Get(stats, "cpu_sample_age_ms");
        var rows = new List<(string, string)>
        {
            ("LED commands", $"{Number(Js.Get(stats, "commands_per_second"))} / {Number(Js.Get(stats, "command_budget"))} per second"),
            ("Acknowledgment · 95th percentile", Finite(ack) ? $"{Number(ack, 2)} ms" : "—"),
            ("CPU usage · last sample", hasCpuSample && Finite(idle) && Js.Num(idle) <= 1 ? $"{Number((1 - Js.Num(idle)!.Value) * 100, 0)}%" : "—"),
            ("Load average", hasCpuSample ? Number(Js.Get(stats, "switch_load"), 2) : "—"),
            ("CPU sample age", Finite(cpuAge) ? $"{Number(Js.Num(cpuAge)!.Value / 1000)} s" : "—"),
        };
        rows.AddRange(resourceRows);
        rows.AddRange(poeRows);
        var rowArray = new JsonArray();
        foreach (var (label, value) in rows) rowArray.Add(new JsonArray(label, value));
        return new JsonObject { ["status"] = statusText, ["rows"] = rowArray };
    }
}
