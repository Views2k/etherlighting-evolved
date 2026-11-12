using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The EE computer's local time for schedules, port of preview/schedule-clock.mjs. Each clock reading is anchored
/// to the moment it arrived, so the time keeps advancing between readings.
/// </summary>
public static partial class ScheduleClock
{
    sealed record Anchor(double Wall, double ReceivedAt);

    static readonly ConditionalWeakTable<JsonNode, Anchor> Anchors = new();
    static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static double Now => Clock.Elapsed.TotalMilliseconds;

    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2})T(\d{2}:\d{2}:\d{2})(\.\d{1,6})?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex LocalIso();

    public static JsonNode? Receive(JsonNode? clock, double? receivedAt = null)
    {
        if (clock is not JsonObject || Anchors.TryGetValue(clock, out _)) return clock;
        var wall = double.NaN;
        if (clock.Str("local_iso") is { } iso && LocalIso().Match(iso) is { Success: true } match
            && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            var fraction = (match.Groups[3].Success ? match.Groups[3].Value : ".000").PadRight(4, '0')[..4];
            if (DateTime.TryParseExact($"{match.Groups[1].Value}T{match.Groups[2].Value}{fraction}", "yyyy-MM-dd'T'HH:mm:ss.fff",
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var civil))
                wall = (civil - DateTime.UnixEpoch).TotalMilliseconds;
        }
        Anchors.AddOrUpdate(clock, new Anchor(wall, receivedAt ?? Now));
        return clock;
    }

    /// <summary>The host's wall-clock time (in its own zone, expressed as UTC fields), or null when unknown.</summary>
    public static DateTime? HostDate(JsonNode? state, double? now = null)
    {
        var at = now ?? Now;
        var clock = state.At("schedule_clock");
        Receive(clock, at);
        if (clock is null || !Anchors.TryGetValue(clock, out var anchor) || double.IsNaN(anchor.Wall)) return null;
        return DateTime.UnixEpoch.AddMilliseconds(anchor.Wall + Math.Max(0, at - anchor.ReceivedAt));
    }

    public static string Label(JsonNode? state)
    {
        if (HostDate(state) is null) return "EE computer";
        return state.Str("schedule_clock.zone_name") is { } label && label.Trim().Length > 0 ? label.Trim() : "EE computer";
    }
}
