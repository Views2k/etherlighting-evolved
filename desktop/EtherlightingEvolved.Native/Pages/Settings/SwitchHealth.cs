using System.Globalization;
using System.Text.Json.Nodes;
using EtherlightingEvolved.Native.Core;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>Port of preview/switch-health.mjs: the Switch health readings shown in Settings.</summary>
public static class SwitchHealth
{
    // The app runs with invariant globalization; its number format matches en-US (comma groups, period decimals).
    static readonly CultureInfo English = CultureInfo.InvariantCulture;

    static bool Finite(double? value) => value is { } number && double.IsFinite(number) && number >= 0;

    /// <summary>toLocaleString('en-US', {maximumFractionDigits}) for a finite, non-negative value; otherwise an em dash.</summary>
    public static string Number(double? value, int digits = 1) =>
        Finite(value) ? Format(value!.Value, digits) : "—";

    public static string Format(double value, int digits) =>
        Math.Round(value, digits, MidpointRounding.AwayFromZero).ToString(digits == 0 ? "#,##0" : "#,##0." + new string('#', digits), English);

    static string Bytes(double? value)
    {
        if (!Finite(value)) return "—";
        var gib = Math.Pow(1024, 3);
        var scale = value >= gib ? gib : Math.Pow(1024, 2);
        var unit = scale == gib ? "GiB" : "MiB";
        return $"{Number(value / scale)} {unit}";
    }

    static string Usage(JsonNode? value)
    {
        var used = value.Num("used_bytes");
        var total = value.Num("total_bytes");
        return value is not null && Finite(used) && Finite(total) && total > 0 && used <= total
            ? $"{Bytes(used)} / {Bytes(total)} · {Number(used / total * 100, 0)}%"
            : "Not reported";
    }

    public static (string Status, List<(string Label, string Value)> Rows) Read(JsonNode? state, bool unavailable = false)
    {
        var stats = state.Obj("stats");
        var idle = stats.Num("switch_cpu_idle");
        var hasCpuSample = Finite(stats.Num("cpu_sample_age_ms"));
        var resources = stats.Obj("switch_health");
        var resourceRows = new List<(string, string)>();
        var temperatures = resources.Arr("temperatures").Where(sensor => sensor.Num("celsius") is not null).ToList();
        if (temperatures.Count > 0)
            foreach (var sensor in temperatures)
                resourceRows.Add(($"Temperature · {(sensor.Str("label") is { Length: > 0 } label ? label : "Switch")}", $"{Format(sensor.Num("celsius")!.Value, 1)} °C"));
        else resourceRows.Add(("Temperature", "Not reported"));
        resourceRows.Add(("Memory used", Usage(resources.At("memory"))));
        var storage = resources.Arr("storage");
        if (storage.Count > 0)
            foreach (var drive in storage) resourceRows.Add(($"Storage · {MountText(drive)}", Usage(drive)));
        else resourceRows.Add(("Storage used", "Not reported"));
        resourceRows.Add(("Resource sample age", Finite(stats.Num("health_sample_age_ms")) ? $"{Number(stats.Num("health_sample_age_ms") / 1000)} s" : "—"));
        var poe = state.At("poe_summary");
        var poeRows = new List<(string, string)>();
        if (poe.Truthy())
        {
            var fresh = !unavailable && state.Truthy("connected") && poe.Str("status") == "ready";
            poeRows.Add(("PoE draw", Finite(poe.Num("draw_w"))
                ? Number(poe.Num("draw_w")) + " W" + (fresh ? "" : " · " + (poe.Str("status") == "partial" ? "Partial sample" : "Last sample"))
                : "Waiting for a sample"));
            poeRows.Add(("PoE budget", Finite(poe.Num("budget_w")) ? Number(poe.Num("budget_w")) + " W" : "Not reported"));
            var headroom = poe.Num("headroom_w");
            poeRows.Add(("PoE headroom", fresh && headroom is not null
                ? headroom < 0 ? Number(-headroom) + " W over budget" : Number(headroom) + " W"
                : "Waiting for a complete sample"));
            foreach (var consumer in poe.Arr("consumers").Take(5))
                poeRows.Add(("PoE · Port " + PortText(consumer), Number(consumer.Num("power_w")) + " W"));
        }
        string status = state.Truthy("reconnect_required") ? "Reconnect the switch to resume lighting."
            : unavailable || !state.Truthy("connected") ? "Last reported readings · switch disconnected"
            : stats.Truthy("health_error") ? "Latest resource update unavailable · showing last readings" : "Switch readings";
        var idleValue = idle;
        var rows = new List<(string, string)>
        {
            ("LED commands", $"{Number(stats.Num("commands_per_second"))} / {Number(stats.Num("command_budget"))} per second"),
            ("Acknowledgment · 95th percentile", Finite(stats.Num("ack_p95_ms")) ? $"{Number(stats.Num("ack_p95_ms"), 2)} ms" : "—"),
            ("CPU usage · last sample", hasCpuSample && Finite(idleValue) && idleValue <= 1 ? $"{Number((1 - idleValue) * 100, 0)}%" : "—"),
            ("Load average", hasCpuSample ? Number(stats.Num("switch_load"), 2) : "—"),
            ("CPU sample age", Finite(stats.Num("cpu_sample_age_ms")) ? $"{Number(stats.Num("cpu_sample_age_ms") / 1000)} s" : "—"),
        };
        rows.AddRange(resourceRows);
        rows.AddRange(poeRows);
        return (status, rows);
    }

    static string MountText(JsonNode? drive) => drive.At("mount") switch
    {
        null => "undefined",
        JsonValue value when value.Str() is { } text => text,
        var other => other.ToJsonString(),
    };

    static string PortText(JsonNode? consumer) => consumer.At("port") switch
    {
        null => "undefined",
        JsonValue value when value.Num() is { } number => number.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.Str() is { } text => text,
        var other => other.ToJsonString(),
    };
}
