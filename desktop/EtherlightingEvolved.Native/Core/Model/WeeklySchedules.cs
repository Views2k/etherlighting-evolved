// Port of the logic of preview/weekly-schedules.mjs: schedule conflicts and draft validation, plus the module's text
// helpers for the next and last run. renderSchedules builds the Automation page and belongs to that page's port.
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtherlightingEvolved.Native.Core.Model;

public static class WeeklySchedules
{
    /// <summary>The weekdays in the editor's order.</summary>
    public static readonly IReadOnlyList<string> Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    /// <summary>scheduleConflicts(draft, schedules): other enabled schedules at the same time on a shared weekday.</summary>
    public static List<JsonObject> ScheduleConflicts(JsonNode? draft, JsonNode? schedules) =>
        Js.Objects(schedules).Where(item => !Js.StrictEquals(item["id"], Js.Get(draft, "id")) && Js.Truthy(item["enabled"]) && Js.Truthy(Js.Get(draft, "enabled"))
                                            && Js.StrictEquals(item["at"], Js.Get(draft, "at"))
                                            && Js.Items(item["days"]).Any(day => Js.Includes(Js.Get(draft, "days"), day))
                                            && !Js.Truthy(item["needs_device_confirmation"])).ToList();

    /// <summary>scheduleDraftError(draft): '' for a complete schedule, otherwise the message.</summary>
    public static string ScheduleDraftError(JsonNode? draft)
    {
        if (Js.Trim(Js.Str(Js.Get(draft, "name")) ?? "").Length == 0) return "Enter a schedule name.";
        if (!Regex.IsMatch(Js.ToStr(Js.Get(draft, "at")), "^([01][0-9]|2[0-3]):[0-5][0-9]$")) return "Choose a valid time.";
        if (Js.Length(Js.Get(draft, "days")) == 0) return "Choose at least one day.";
        var action = Js.Get(draft, "action");
        if (Js.StrictEquals(Js.Get(action, "type"), "preset") && !Js.Truthy(Js.Get(action, "profile_id"))) return "Choose a saved profile.";
        return "";
    }

    /// <summary>The module's when(value): a run time with "T" replaced by a space, or "—".</summary>
    public static string When(JsonNode? value)
    {
        if (!Js.Truthy(value)) return "—";
        var text = Js.ToStr(value);
        var at = text.IndexOf('T');
        return at < 0 ? text : text[..at] + " " + text[(at + 1)..];
    }

    /// <summary>The module's resultText(value): the last run's result in words.</summary>
    public static string ResultText(JsonNode? value) =>
        Js.StrictEquals(value, "ok") ? "Completed" : Js.StrictEquals(value, "pending") ? "Started; awaiting confirmation" : Js.Truthy(value) ? Js.ToStr(value) : "No runs yet";
}
