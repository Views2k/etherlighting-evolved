using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core;

internal readonly record struct BandwidthPoint(double Offset, double Up, double Down);
internal sealed record UplinkSample(int Sequence, int[] Ports, double Download, double Upload, List<BandwidthPoint> History,
                                   long Series, double CapturedAt, double Interval, double Period);

internal sealed class UplinkMeasurements
{
    string? workspaceId, generation;
    bool knownGeneration;
    long series;
    JsonObject? previousReading;
    public UplinkSample? Last { get; private set; }
    public double ExpiresAt { get; private set; }
    public string EmptyStatus { get; private set; } = "waiting";

    public void Reset()
    {
        Clear("waiting");
        workspaceId = generation = null;
        knownGeneration = false;
        previousReading = null;
    }

    void Clear(string status)
    {
        if (Last is not null) series++;
        Last = null;
        EmptyStatus = status;
    }

    public void Receive(JsonObject state, string? workspace, bool disconnected, long now)
    {
        if (workspaceId != workspace)
        {
            Clear("waiting");
            knownGeneration = false;
            workspaceId = workspace;
        }
        if (state["telemetry"] is JsonObject telemetry && telemetry.TryGetPropertyValue("generation", out var token))
        {
            var current = token.Str();
            if (knownGeneration && generation != current) Clear("waiting");
            knownGeneration = true;
            generation = current;
        }
        var reading = state["uplink_bandwidth"] as JsonObject;
        var repeated = ReferenceEquals(reading, previousReading);
        previousReading = reading;
        if (disconnected || state.Bool("connected") != true) { Clear("unavailable"); return; }
        if (repeated || reading is null) return;

        var status = reading.Str("status");
        if (status is "waiting" or "no_uplink") { Clear(status); return; }
        var ports = reading["ports"] as JsonArray;
        var hasPorts = ports is { Count: > 0 } && ports.All(port => port.Int() > 0);
        var sequence = reading.Int("sequence");
        if (Last is { } last && (sequence < last.Sequence || hasPorts && !SamePorts(ports!, last.Ports))) Clear("waiting");
        EmptyStatus = status == "stale" ? "stale" : "unavailable";
        var age = reading.Num("sample_age_ms");
        if (status == "stale" || status == "ready" && age >= 10_000)
        {
            ExpiresAt = Math.Min(ExpiresAt, now);
            EmptyStatus = "stale";
            return;
        }
        if (status != "ready" || !hasPorts || sequence is null || age is not >= 0 || reading.Num("sample_period_ms") is not > 0
            || reading.Num("download_bps") is not (>= 0 and { } down) || reading.Num("upload_bps") is not (>= 0 and { } up)) return;

        // Every age refers to capture time. A repeated reading can shorten its deadline but
        // cannot restart it or charge time spent in transit twice.
        var deadline = now + 10_000 - age.Value;
        if (Last is { } same && same.Sequence == sequence && same.Download == down && same.Upload == up)
        {
            ExpiresAt = Math.Min(ExpiresAt, deadline);
            return;
        }
        var history = new List<BandwidthPoint>();
        foreach (var row in reading.Arr("history").OfType<JsonArray>())
            if (row.Count == 3 && row[0].Num() is <= 0 and > -60_000 and { } offset &&
                row[1].Num() is >= 0 and { } sent && row[2].Num() is >= 0 and { } received)
                history.Add(new(offset, sent, received));
        var period = reading.Num("sample_period_ms")!.Value;
        var interval = reading.Num("refresh_interval_ms") is > 0 and { } advertised ? advertised : period;
        Last = new(sequence.Value, ports!.Select(p => p.Int()!.Value).ToArray(), down, up, history,
                   series, now - age.Value, interval, period);
        ExpiresAt = deadline;
    }

    static bool SamePorts(JsonArray ports, int[] previous) => ports.Count == previous.Length
        && ports.Select(p => p.Int()!.Value).All(previous.Contains);
}
