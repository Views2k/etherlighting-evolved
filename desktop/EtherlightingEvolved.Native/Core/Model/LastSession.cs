// Port of the logic of preview/last-session.mjs: the Settings readout of how the previous session ended.
// mountLastSession builds the section's DOM and requests /api/last-session; it belongs to the Settings page's port.
using System.Globalization;
using System.Text.Json.Nodes;

namespace EtherlightingEvolved.Native.Core.Model;

public static class LastSession
{
    static readonly Dictionary<string, string> Stages = new(StringComparer.Ordinal)
    {
        ["engine"] = "Lighting service", ["shutdown"] = "Workspace shutdown", ["parent_cleanup"] = "Desktop connection lost", ["desktop_exit"] = "Desktop shutdown",
    };

    static readonly Dictionary<string, string> Outcomes = new(StringComparer.Ordinal)
    {
        ["closed"] = "Cleanup confirmed", ["failed"] = "Service stopped with an error", ["cleanup_unconfirmed"] = "Lighting cleanup needs review",
    };

    /// <summary>Date.parse for the ISO times the service writes; null when the text is not a date.</summary>
    static DateTimeOffset? ParseTime(JsonNode? value)
    {
        // A time without an offset is read as local time, as Date.parse reads a date-time without one.
        if (Js.Str(value) is not { } text) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed) ? parsed : null;
    }

    /// <summary>lastSessionView(payload): {rows?: [[label, value]], message}.</summary>
    public static JsonObject LastSessionView(JsonNode? payload)
    {
        var entry = Js.Get(payload, "last_session");
        if (!Js.IsTrue(Js.Get(payload, "available"))) return new JsonObject { ["message"] = "Session details could not be read. Try Refresh." };
        if (!Js.Truthy(entry)) return new JsonObject { ["message"] = "No earlier session has been recorded." };
        var stage = Js.Str(Js.Get(entry, "stage"));
        var outcome = Js.Str(Js.Get(entry, "outcome"));
        var time = ParseTime(Js.Get(entry, "time"));
        if (stage is null || !Stages.TryGetValue(stage, out var stageText) || outcome is null || !Outcomes.TryGetValue(outcome, out var outcomeText) || time is null)
            return new JsonObject { ["message"] = "Session details could not be read. Try Refresh." };
        var rows = new JsonArray
        {
            new JsonArray("Time", time.Value.ToLocalTime().ToString("M/d/yyyy, h:mm:ss tt", CultureInfo.InvariantCulture)),
            new JsonArray("Stage", stageText),
            new JsonArray("Result", outcomeText),
            new JsonArray("Version", Js.Truthy(Js.Get(entry, "build")) ? Js.ToStr(Js.Get(entry, "build")) : "Unknown"),
        };
        if (Js.IsSafeInteger(Js.Get(entry, "attempts"))) rows.Add(new JsonArray("Cleanup attempts", Js.ToStr(Js.Get(entry, "attempts"))));
        return new JsonObject { ["rows"] = rows, ["message"] = outcome == "closed" ? "" : "Review the connection status before resuming lighting." };
    }
}
