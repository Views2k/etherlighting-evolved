// Port of the logic exports of preview/port-animation.mjs that the port map uses: color parsing and the confirmed-frame
// buffer that plays live colors back at an even pace. createPortAnimator drives DOM elements and belongs to the page
// that shows the port map.
using System.Globalization;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class PortAnimation
{
    static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));

    internal static bool RgbChannels(object? value) => value is double[] { Length: 3 } channels && channels.All(double.IsFinite);

    static bool HexColor(object? value) => value is string { Length: 7 } text && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit);

    /// <summary>The module's rgb(value): finite channel triples clamped, six-digit hex parsed, anything else black.</summary>
    internal static double[] Rgb(object? value)
    {
        if (value is JsonNode node) value = Effects.Paint(node);
        if (RgbChannels(value)) return ((double[])value!).Select(channel => Clamp(channel, 0, 255)).ToArray();
        if (HexColor(value))
        {
            var text = (string)value!;
            return new[] { 1, 3, 5 }.Select(offset => (double)int.Parse(text.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        }
        return [0, 0, 0];
    }
}

/// <summary>
/// ColorFrames: buffers confirmed frames and samples them with a delay that follows the arrival cadence. Slow arrivals
/// never grant permission to predict output.
/// </summary>
public sealed class ColorFrames
{
    public sealed record Frame(double Time, Dictionary<string, double[]> Colors);

    static double Clamp(double value, double low, double high) => Math.Max(low, Math.Min(high, value));

    public ColorFrames(double delay = 120)
    {
        MinimumDelay = Clamp(double.IsFinite(delay) ? delay : 120, 0, 500);
        Delay = MinimumDelay;
    }

    public double MinimumDelay { get; }

    public double Delay { get; private set; }

    public List<Frame> Frames { get; } = [];

    public List<double> Intervals { get; } = [];

    double? playback, sampledAt;

    static Dictionary<string, double[]>? ColorRecord(IEnumerable<KeyValuePair<string, object?>> value)
    {
        var entries = value.ToList();
        if (entries.Any(entry => !PortAnimation.RgbChannels(entry.Value) && !(entry.Value is string { Length: 7 } text && text[0] == '#' && text.Skip(1).All(char.IsAsciiHexDigit))))
            return null;
        return entries.ToDictionary(entry => entry.Key, entry => PortAnimation.Rgb(entry.Value));
    }

    /// <summary>push(colors, time): adds a confirmed frame; false when it is older than the last one or not valid colors.</summary>
    public bool Push(IEnumerable<KeyValuePair<int, double[]>> colors, double time) =>
        Push(colors.Select(pair => new KeyValuePair<string, object?>(pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value)), time);

    public bool Push(JsonObject colors, double time) =>
        Push(Js.Entries(colors).Select(entry => new KeyValuePair<string, object?>(entry.Key, Effects.Paint(entry.Value))), time);

    public bool Push(IEnumerable<KeyValuePair<string, object?>> colors, double time)
    {
        if (!double.IsFinite(time)) return false;
        var previous = Frames.Count > 0 ? Frames[^1] : null;
        if (previous is not null && time <= previous.Time) return false;
        var decoded = ColorRecord(colors);
        if (decoded is null) return false;
        if (previous is not null)
        {
            var interval = time - previous.Time;
            if (interval <= 1000)
            {
                Intervals.Add(interval);
                if (Intervals.Count > 24) Intervals.RemoveAt(0);
                Delay = Clamp(Intervals.Max() + 50, MinimumDelay, 500);
            }
            else
            {
                Intervals.Clear();
                Delay = MinimumDelay;
                playback = null;
                sampledAt = null;
            }
        }
        Frames.Add(new Frame(time, decoded));
        while (Frames.Count > 2 && Frames[1].Time < time - 1200) Frames.RemoveAt(0);
        return true;
    }

    /// <summary>latest(): the newest confirmed colors.</summary>
    public Dictionary<string, double[]> Latest() =>
        Frames.Count > 0 ? Frames[^1].Colors.ToDictionary(pair => pair.Key, pair => pair.Value) : [];

    /// <summary>sample(now): the colors to show now, interpolated between confirmed frames.</summary>
    public Dictionary<string, double[]> Sample(double now)
    {
        if (Frames.Count == 0) return [];
        var desired = now - Delay;
        double? elapsed = sampledAt is { } at ? now - at : null;
        if (elapsed is not { } step || step < 0 || step > 1000) playback = desired;
        else
        {
            var correction = Clamp((desired - playback!.Value - step) * (1 - Math.Exp(-step / 150)), -step * .1, step * .1);
            playback += step + correction;
        }
        sampledAt = now;
        var time = Math.Min(playback!.Value, Frames[^1].Time);
        playback = time;
        var before = Frames[0];
        var after = before;
        foreach (var frame in Frames)
        {
            after = frame;
            if (frame.Time >= time) break;
            before = frame;
        }
        var amount = after.Time == before.Time ? 1 : Clamp((time - before.Time) / (after.Time - before.Time), 0, 1);
        return after.Colors.ToDictionary(pair => pair.Key, pair =>
        {
            var start = before.Colors.TryGetValue(pair.Key, out var earlier) ? earlier : pair.Value;
            return start.Select((channel, index) => channel + (pair.Value[index] - channel) * amount).ToArray();
        });
    }
}
