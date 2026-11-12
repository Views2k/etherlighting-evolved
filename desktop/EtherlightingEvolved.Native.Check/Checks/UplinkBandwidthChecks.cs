using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages.Lighting;

namespace EtherlightingEvolved.Native.Check.Checks;

internal static class UplinkBandwidthChecks
{
    public static void Run(Action<bool, string> check)
    {
        var meter = new UplinkBandwidthMeter();
        meter.ShowChart(true);
        var sample = Json.Parse("""{"status":"ready","sequence":7,"ports":[3],"sample_period_ms":5000,"sample_age_ms":1000,"download_bps":4250000,"upload_bps":500000,"history":[[-5000,1000000,5600000],[0,500000,4250000]]}""").Obj();
        var plot = meter.GetLogicalDescendants().OfType<BandwidthPlot>().Single();
        var labels = meter.GetLogicalDescendants().OfType<TextBlock>().ToArray();
        bool Has(string label) => labels.Any(t => t.Text == label);
        var down = 4.25.ToString("0.00");
        var peak = $"Peak {5.6:0.00} Mbps";
        var measurements = new UplinkMeasurements();
        var frame = new JsonObject();
        double? Show(JsonObject? reading, long now, bool connected = true, string generation = "generation1", string workspace = "first", bool repeat = false)
        {
            if (!repeat) frame["uplink_bandwidth"] = reading?.DeepClone();
            frame["connected"] = connected;
            frame["telemetry"] = new JsonObject { ["generation"] = generation };
            measurements.Receive(frame, workspace, false, now);
            return meter.RenderReading(measurements, now);
        }
        Size Measure()
        {
            meter.Measure(new Size(760, 200));
            meter.Arrange(new Rect(meter.DesiredSize));
            return meter.Bounds.Size;
        }

        check(Show(sample, 1000) == 10000 && Has(down) && Has(peak) && plot.HasHistory,
            "the meter shows the confirmed rates, minute peaks and history");
        var size = Measure();
        sample["sample_age_ms"] = 6000;
        check(Show(sample, 6000) == 10000 && !Has("Last reading") && Has(down),
            "increasing server age on the same sample does not expire the reading early");
        check(Show(null, 7000) == 10000 && Show(new JsonObject { ["status"] = "unavailable", ["ports"] = new JsonArray(3) }, 7500) == 10000
            && Has(peak) && !Has("Last reading"), "missing or temporarily unavailable measurements preserve a fresh rate until its deadline");
        check(Show(new JsonObject { ["status"] = "ready", ["ports"] = new JsonArray(3) }, 9999) == 10000 &&
            meter.RenderReading(measurements, 10000) is null && Has("Last reading") && Has(down) && Has(peak),
            "repainting a paused stream expires once at its original deadline and keeps the last rates and peaks");
        var stale = Json.Parse("""{"status":"stale","sequence":7,"ports":[3],"history":[]}""").Obj();
        Show(stale, 12000);
        check(plot.HasHistory && Has(down) && Has("Last reading") && Has("Minute before last reading") && Measure() == size &&
            meter.GetLogicalDescendants().OfType<TextBlock>().SequenceEqual(labels),
            "an explicit stale report preserves the same readouts, plot and layout");
        sample["sequence"] = 8; sample["sample_age_ms"] = 0; sample["download_bps"] = 0;
        Show(sample, 14000);
        check(Has(0d.ToString("0.00")) && !Has("Last reading") && Measure() == size,
            "a new real zero replaces a delayed rate without resizing");
        var waiting = Json.Parse("""{"status":"waiting","sequence":8,"ports":[3]}""").Obj();
        Show(waiting, 14001);
        check(Has("Waiting for reading") && !Has(peak) && !plot.HasHistory && Measure() == size,
            "a confirmed counter reset clears previous rates and history for the same ports");

        Show(sample, 15000);
        Show(sample, 16000, false);
        Show(sample, 16001, repeat: true);
        check(Has("Unavailable") && !Has(peak) && !plot.HasHistory, "reconnect requires a new snapshot before displaying the old switch rates");
        var fresh = sample.DeepClone().AsObject();
        Show(fresh, 16002);
        check(Has(peak), "a new snapshot after reconnect restores confirmed rates");
        Show(stale, 16100, generation: "generation2");
        check(!Has(peak) && !plot.HasHistory, "a different telemetry generation cannot inherit a last reading");
        Show(fresh, 16200);
        Show(fresh, 16201, workspace: "second", repeat: true);
        check(!Has(peak), "a new workspace cannot use bandwidth retained in an old partial frame");
        Show(sample, 16300);
        stale["sequence"] = 8; stale["ports"] = new JsonArray(4);
        Show(stale, 16301);
        check(!Has(peak) && !plot.HasHistory, "a different uplink cannot inherit a delayed reading from the previous port");
        Show(sample, 16400);
        var reopened = new UplinkBandwidthMeter();
        reopened.RenderReading(measurements, 26401);
        check(reopened.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Last reading"),
            "opening a new Lighting page reuses the original sample deadline");
        Show(new JsonObject { ["status"] = "no_uplink", ["ports"] = new JsonArray() }, 16401);
        check(Has("No uplink detected") && !Has(peak) && !plot.HasHistory, "removing the last uplink clears its values");
        GraphMotion(check);
        RefreshOrdering(check);
    }

    static void GraphMotion(Action<bool, string> check)
    {
        static UplinkSample Sample(int sequence, double captured, params BandwidthPoint[] history) =>
            new(sequence, [3], history[^1].Down, history[^1].Up, [..history], 1, captured, 1000, 1000);
        var first = Sample(4, 5000, new(-3000, 10, 20), new(-2000, 30, 40), new(-1000, 10, 20), new(0, 40, 80));
        var trace = new BandwidthTimeline();
        trace.Update(first, 5000);
        var start = trace.Cursor(5000);
        check(trace.Cursor(5030) == start + 30 && trace.Cursor(5060) == start + 60 && start < 5000,
            "the download and upload time axis advances uniformly between readings");
        var following = Sample(5, 6055, new(-3000, 30, 40), new(-2000, 10, 20), new(-1000, 40, 80), new(0, 50, 900));
        var before = trace.Cursor(6060);
        var scale = trace.Scale(6060);
        trace.Update(following, 6060);
        check(trace.Cursor(6060) == before && trace.Scale(6060) == scale && trace.Scale(6280) > scale &&
            trace.Scale(6280) < trace.FinalScale, "arrival jitter and a new peak preserve the current graph position and scale");
        check(trace.Cursor(9000) == 6000 && !trace.Moving(9000) && trace.Offset(9000) == 0,
            "the graph stops at the last actual capture when no further counters arrive");
        var missed = Sample(6, 9500, new(-5500, 10, 20), new(-4500, 40, 80), new(-3500, 50, 900), new(0, 30, 50))
            with { Period = 3500 };
        trace.Update(missed, 9500);
        check(!trace.Joins(-3500, 0) && trace.Joins(-4500, -3500), "missing intervals break the trace without widening their own gap threshold");
        trace.Update(first with { Series = 2, CapturedAt = 11_000 }, 11_000);
        check(trace.Points.Count == first.History.Count && trace.Cursor(20_000) == 11_000, "a new source has an independent capture clock and history");

        var now = 5000L;
        var plot = new BandwidthPlot(() => now);
        var window = new Window { Content = plot };
        try
        {
            plot.Update(first, now);
            plot.SetDeadline(15_000, now);
            plot.SetActive(true);
            check(!plot.IsAnimating, "a detached bandwidth graph schedules no animation");
            window.Show();
            plot.SetDeadline(15_000, now);
            check(plot.IsAnimating, "a visible bandwidth graph advances independently of the switch sampling rate");
            plot.SetActive(false);
            check(!plot.IsAnimating, "hiding or leaving the chart stops its rendering timer");
            plot.SetActive(true);
            plot.SetDeadline(now, now);
            check(!plot.IsAnimating, "stale bandwidth readings freeze the chart");
            plot.SetDeadline(15_000, now);
            window.Content = null;
            check(!plot.IsAnimating, "detaching a running graph releases its rendering timer");
        }
        finally { window.Close(); }
    }

    static void RefreshOrdering(Action<bool, string> check)
    {
        var workspace = new Workspace(new Uri("http://127.0.0.1/")) { Ready = true };
        JsonObject Snapshot(int seq, int rate) => new()
        {
            ["connected"] = true, ["brightness"] = 51,
            ["telemetry"] = new JsonObject { ["generation"] = "sample-source" },
            ["uplink_bandwidth"] = new JsonObject
            {
                ["status"] = "ready", ["sequence"] = seq, ["ports"] = new JsonArray(3), ["sample_age_ms"] = 1000,
                ["sample_period_ms"] = 5000, ["upload_bps"] = rate, ["download_bps"] = rate,
            }
        };
        var slowHttp = workspace.BeginRefresh();
        var live = Snapshot(8, 800);
        live.Remove("brightness"); live["type"] = "frame"; live["ports"] = new JsonArray(3);
        workspace.Handle(live, 24_000);
        workspace.ApplyRefresh(slowHttp, Snapshot(7, 700), new JsonArray(1), 10_000, false);
        check(workspace.Bandwidth.Last?.Upload == 800 && workspace.Bandwidth.ExpiresAt == 33_000 &&
            workspace.State.Int("brightness") == 51 && workspace.State.Int("ports.0") == 3,
            "a late HTTP refresh keeps the newer socket rate, receipt deadline and ports while filling in the full configuration");

        slowHttp = workspace.BeginRefresh();
        workspace.Handle(new JsonObject { ["type"] = "frame", ["connected"] = false, ["uplink_bandwidth"] = null }, 35_000);
        workspace.ApplyRefresh(slowHttp, Snapshot(8, 800), new JsonArray(3), 33_500, false);
        check(!workspace.Connected && workspace.Bandwidth.Last is null && workspace.State["uplink_bandwidth"] is null,
            "an older connected HTTP snapshot cannot restore bandwidth after a live disconnect");

        slowHttp = workspace.BeginRefresh();
        workspace.ApplyRefresh(slowHttp, Snapshot(1, 100), new JsonArray(3), 40_000, false);
        var meter = new UplinkBandwidthMeter();
        check(meter.RenderReading(workspace.Bandwidth, 52_000) is null && workspace.Bandwidth.ExpiresAt == 49_000 &&
            meter.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Last reading"),
            "a delayed ports request cannot make a twelve-second-old HTTP state reading fresh");

        slowHttp = workspace.BeginRefresh();
        var newState = Snapshot(2, 200); newState["brightness"] = 80;
        workspace.Handle(new JsonObject { ["type"] = "state", ["state"] = newState }, 55_000);
        workspace.ApplyRefresh(slowHttp, Snapshot(1, 100), new JsonArray(3), 54_000, false);
        check(workspace.State.Int("brightness") == 80 && workspace.Bandwidth.Last?.Upload == 200 && workspace.State.Int("ports.0") == 3,
            "a newer full state retains its configuration and accepts the ports reply it did not supply");
        workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
