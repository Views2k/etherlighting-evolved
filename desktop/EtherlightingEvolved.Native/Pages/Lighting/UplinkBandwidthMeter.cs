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
    readonly TextBlock heading = U.H3("Uplink bandwidth");
    readonly TextBlock chartLabel = U.Text("Last minute", "small muted");
    readonly TextBlock downloadLabel = U.Text("↓ Download", "small muted"), uploadLabel = U.Text("↑ Upload", "small muted");
    readonly TextBlock downloadUnit = U.Text("Mbps", "small muted"), uploadUnit = U.Text("Mbps", "small muted");
    Control chart = new Panel();
    UplinkSample? displayed;
    bool pageVisible = true, compact, chartVisible;
    double renderedDeadline;

    public UplinkBandwidthMeter()
    {
        ColumnSpacing = 16;
        foreach (var rate in new[] { download, upload })
        {
            rate.FontSize = 16;
            rate.FontWeight = FontWeight.SemiBold;
        }
        downloadUnit.VerticalAlignment = uploadUnit.VerticalAlignment = VerticalAlignment.Bottom;
        chartLabel.VerticalAlignment = VerticalAlignment.Center;
        Arrange();
    }

    /// <summary>
    /// The compact form is one line for short, wide windows: the title, both readings and the chart side by side.
    /// </summary>
    public void SetCompact(bool value)
    {
        if (compact == value) return;
        compact = value;
        Arrange();
    }

    void Arrange()
    {
        foreach (var leaf in new Control[] { heading, status, downloadLabel, download, downloadUnit, downloadPeak, uploadLabel, upload, uploadUnit, uploadPeak, chartLabel, plot })
            (leaf.Parent as Panel)?.Children.Remove(leaf);
        Children.Clear();
        if (compact)
        {
            ColumnDefinitions = new("Auto,Auto,Auto,*");
            ColumnSpacing = 22;
            var title = U.Column(1, heading, status);
            var down = U.Column(1, U.Row(6, downloadLabel, download, downloadUnit), downloadPeak);
            var up = U.Column(1, U.Row(6, uploadLabel, upload, uploadUnit), uploadPeak);
            downloadLabel.VerticalAlignment = uploadLabel.VerticalAlignment = VerticalAlignment.Bottom;
            var line = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            line.Children.Add(chartLabel);
            line.Children.Add(plot);
            SetColumn(plot, 1);
            chart = line;
            foreach (var (part, column) in new (Control, int)[] { (title, 0), (down, 1), (up, 2), (chart, 3) })
            {
                part.VerticalAlignment = VerticalAlignment.Center;
                Children.Add(part);
                SetColumn(part, column);
            }
        }
        else
        {
            ColumnDefinitions = new("Auto,Auto");
            ColumnSpacing = 16;
            var header = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
            header.Children.Add(heading);
            header.Children.Add(status);
            SetColumn(status, 1);
            var metrics = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 16, Width = 270 };
            downloadLabel.VerticalAlignment = uploadLabel.VerticalAlignment = VerticalAlignment.Top;
            var down = U.Column(1, downloadLabel, U.Row(5, download, downloadUnit), downloadPeak);
            var up = U.Column(1, uploadLabel, U.Row(5, upload, uploadUnit), uploadPeak);
            metrics.Children.Add(down);
            metrics.Children.Add(up);
            SetColumn(up, 1);
            Children.Add(U.Column(4, header, metrics));
            var column = U.Column(2, chartLabel, plot);
            column.VerticalAlignment = VerticalAlignment.Center;
            chart = column;
            Children.Add(chart);
            SetColumn(chart, 1);
        }
        ShowChart(chartVisible);
    }

    public void ShowChart(bool visible)
    {
        chartVisible = visible;
        chart.IsVisible = visible;
        if (!compact) ColumnDefinitions[1].Width = visible ? GridLength.Star : GridLength.Auto;
        plot.SetActive(pageVisible && visible);
    }

    public void SetPageVisible(bool visible)
    {
        pageVisible = visible;
        plot.SetActive(visible && chart.IsVisible);
    }

    public bool NeedsReading(UplinkMeasurements measurements) =>
        !ReferenceEquals(measurements.Last, displayed) || renderedDeadline != measurements.ExpiresAt;

    public double? RenderReading(UplinkMeasurements measurements, long now)
    {
        var reading = measurements.Last;
        renderedDeadline = measurements.ExpiresAt;
        if (!ReferenceEquals(reading, displayed))
        {
            displayed = reading;
            download.Text = reading is null ? "—" : Rate(reading.Download);
            upload.Text = reading is null ? "—" : Rate(reading.Upload);
            var history = reading?.History ?? [];
            downloadPeak.Text = history.Count > 0 ? $"Peak {Rate(history.Max(p => p.Down))} Mbps" : "Peak —";
            uploadPeak.Text = history.Count > 0 ? $"Peak {Rate(history.Max(p => p.Up))} Mbps" : "Peak —";
            plot.Update(reading, now);
        }
        plot.SetDeadline(reading is null ? 0 : measurements.ExpiresAt, now);
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
