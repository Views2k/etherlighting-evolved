using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using EtherlightingEvolved.Native.Core;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Lighting;

internal sealed class UplinkBandwidthMeter : Grid
{
    readonly TextBlock status = U.Text("", "small muted");
    readonly TextBlock download = U.Text("—"), upload = U.Text("—");
    readonly TextBlock downloadPeak = U.Text("Peak —", "small muted"), uploadPeak = U.Text("Peak —", "small muted");
    readonly BandwidthPlot plot = new();
    readonly StackPanel chart;
    readonly TextBlock chartLabel = U.Text("Last minute", "small muted");
    UplinkSample? displayed;

    public UplinkBandwidthMeter()
    {
        ColumnDefinitions = new("Auto,Auto");
        ColumnSpacing = 16;
        var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(U.H3("Uplink bandwidth"));
        header.Children.Add(status);
        SetColumn(status, 1);
        var metrics = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16, Width = 270 };
        var down = Metric("↓ Download", download, downloadPeak);
        var up = Metric("↑ Upload", upload, uploadPeak);
        metrics.Children.Add(down);
        metrics.Children.Add(up);
        SetColumn(up, 1);
        Children.Add(U.Column(4, header, metrics));
        chart = U.Column(2, chartLabel, plot);
        chart.VerticalAlignment = VerticalAlignment.Center;
        Children.Add(chart);
        SetColumn(chart, 1);
        ShowChart(false);
    }

    static StackPanel Metric(string label, TextBlock rate, TextBlock peak)
    {
        rate.FontSize = 16;
        rate.FontWeight = FontWeight.SemiBold;
        var unit = U.Text("Mbps", "small muted");
        unit.VerticalAlignment = VerticalAlignment.Bottom;
        return U.Column(1, U.Text(label, "small muted"), U.Row(5, rate, unit), peak);
    }

    public void ShowChart(bool visible) => chart.IsVisible = visible;

    public double? RenderReading(UplinkMeasurements measurements, long now)
    {
        var reading = measurements.Last;
        if (!ReferenceEquals(reading, displayed))
        {
            displayed = reading;
            download.Text = reading is null ? "—" : Rate(reading.Download);
            upload.Text = reading is null ? "—" : Rate(reading.Upload);
            var history = reading?.History ?? [];
            downloadPeak.Text = history.Count > 0 ? $"Peak {Rate(history.Max(p => p.Down))} Mbps" : "Peak —";
            uploadPeak.Text = history.Count > 0 ? $"Peak {Rate(history.Max(p => p.Up))} Mbps" : "Peak —";
            plot.Update(history);
        }
        var delayed = reading is not null && now >= measurements.ExpiresAt;
        status.Text = reading is null ? measurements.EmptyStatus switch
        {
            "waiting" => "Waiting for reading",
            "no_uplink" => "No uplink detected",
            "stale" => "Readings delayed",
            _ => "Unavailable",
        } : delayed ? "Last reading" : "";
        chartLabel.Text = delayed ? "Minute before last reading" : "Last minute";
        return reading is not null && !delayed ? measurements.ExpiresAt : null;
    }
    static string Rate(double bps) => bps is > 0 and < 10_000 ? "<0.01" : (bps / 1_000_000).ToString(bps >= 1e9 ? "0" : "0.00");
}
