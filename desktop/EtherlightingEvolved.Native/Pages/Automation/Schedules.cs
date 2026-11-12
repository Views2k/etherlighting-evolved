using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

internal sealed partial class AutomationPage
{
    JsonArray schedules = [], profiles = [];
    JsonObject schedule = [];
    JsonNode? clock;
    string? scheduleId;
    bool historyAvailable;
    JsonObject? Existing => schedules.Objects().FirstOrDefault(item => item.Str("id") == scheduleId);
    const string Endpoint = "/api/schedules";
    string SchedulePath => Endpoint + "/" + Uri.EscapeDataString(scheduleId!);

    async Task LoadSchedules()
    {
        var stored = Get(Endpoint); var saved = Get("/api/profiles");
        await Task.WhenAll(stored, saved);
        if (!Active) return;
        var result = await stored;
        schedules = result.Arr("schedules"); profiles = (await saved).Arr("profiles"); clock = result.At("clock");
        historyAvailable = result.Bool("history_available") != false;
        scheduleId = Existing?.Str("id") ?? schedules.FirstOrDefault().Str("id");
        ResetSchedule();
    }
    void ResetSchedule()
    {
        var current = Existing;
        schedule = current == null ? new JsonObject
        {
            ["name"] = "",
            ["at"] = "19:00",
            ["enabled"] = true,
            ["days"] = Strings(WeeklySchedules.Days),
            ["action"] = new JsonObject { ["type"] = "brightness", ["value"] = 50 }
        } :
            new JsonObject
            {
                ["name"] = current["name"].Copy(),
                ["at"] = current["at"].Copy(),
                ["enabled"] = current["enabled"].Copy(),
                ["days"] = current["days"].Copy(),
                ["action"] = current["action"].Copy(),
                ["id"] = current["id"].Copy(),
                ["expected_revision"] = current["revision"].Copy()
            };
        edited = false;
    }
    async Task SelectSchedule(string? id)
    {
        if (!await Discard()) return;
        scheduleId = id; ResetSchedule(); Draw();
    }
    void DrawSchedules()
    {
        Body.Children.Add(Header("Automation", "Run your lighting setup on a weekly schedule.",
            ActionButton("Refresh schedules", async () => { if (await Discard()) await Load(); }), ActionButton("New schedule", () => SelectSchedule(null), "primary")));
        if (!historyAvailable) Body.Children.Add(U.P("Schedule history needs review before schedules can run.", "error"));
        var list = U.Column(8);
        foreach (var row in schedules.Objects())
        {
            var id = row.Str("id")!;
            var status = row.Bool("needs_device_confirmation") == true ? "Review for connected switch" : row.Bool("profile_missing") == true
                ? "Choose a saved profile" : row.Bool("enabled") == true ? "Enabled" : "Disabled";
            list.Children.Add(ListItem(row.Str("name")!, $"{row.Str("at")} · {string.Join(", ", Values(row["days"]).Select(Title))} · {status}",
                id == scheduleId, () => Run(() => SelectSchedule(id))));
        }
        if (schedules.Count == 0) list.Children.Add(U.P("Add a schedule to run a profile or change brightness at a chosen time.", "muted"));
        var editor = U.Column(12, U.H2(Existing == null ? "New schedule" : "Edit schedule"));
        var clockState = clock == null ? Workspace.State : new JsonObject { ["schedule_clock"] = clock.Copy() };
        editor.Children.Add(U.P($"Time on the EE computer · {ScheduleClock.Label(clockState)}", "muted"));
        void Set(string key, JsonNode? value) { if (!JsonNode.DeepEquals(schedule[key], value)) { schedule[key] = value; edited = true; } }
        var name = U.Input(schedule.Str("name"), changed: v => Set("name", v));
        name.MaxLength = 60;
        Avalonia.Automation.AutomationProperties.SetName(name, "Schedule name");
        editor.Children.Add(U.Field("Schedule name", name));
        var time = new TimePicker { ClockIdentifier = "24HourClock" };
        if (TimeSpan.TryParseExact(schedule.Str("at"), @"hh\:mm", CultureInfo.InvariantCulture, out var start)) time.SelectedTime = start;
        time.SelectedTimeChanged += (_, _) => Set("at", time.SelectedTime?.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        editor.Children.Add(U.Wrap(10, U.Field("Time", time), U.Check("Enabled", schedule.Bool("enabled") == true, v => Set("enabled", v))));
        var dayControls = U.Wrap(6); var selectedDays = Values(schedule["days"]).ToHashSet();
        foreach (var day in WeeklySchedules.Days) dayControls.Children.Add(U.Check(Title(day), selectedDays.Contains(day), on =>
        {
            if (on) selectedDays.Add(day); else selectedDays.Remove(day); Set("days", Strings(WeeklySchedules.Days.Where(selectedDays.Contains)));
        }));
        editor.Children.Add(U.Field("Days", dayControls));
        var action = schedule.Obj("action");
        var type = action.Str("type");
        editor.Children.Add(U.Field("Action", U.Select([("preset", "Apply profile"), ("brightness", "Set brightness"), ("release", "Hand off lighting to UniFi"), ("resume", "Resume EE lighting")],
            type, mode =>
            {
                if (mode == type) return;
                var value = new JsonObject { ["type"] = mode };
                if (mode == "preset") value["profile_id"] = ""; else if (mode == "brightness") value["value"] = 50;
                Set("action", value); Draw();
            })));
        if (type == "preset")
        {
            var options = new List<(string, string)> { ("", "Choose a profile") };
            options.AddRange(profiles.Objects().Select(r => (r.Str("id")!, r.Str("name")!)));
            editor.Children.Add(U.Field("Profile", U.Select(options, action.Str("profile_id"), v => { action["profile_id"] = v; edited = true; })));
        }
        if (type == "brightness")
        {
            var value = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 1, Value = action.Int("value") ?? 50, FormatString = "0" };
            value.ValueChanged += (_, _) => { if (value.Value != null) { action["value"] = (int)value.Value.Value; edited = true; } };
            editor.Children.Add(U.Field("Brightness · percent", value));
        }
        var current = Existing;
        editor.Children.Add(U.P(current.Bool("needs_device_confirmation") == true ? "Saving confirms this schedule for the connected switch." : "This schedule is saved for the connected switch.", "muted"));
        editor.Children.Add(U.P($"Next run: {WeeklySchedules.When(current.At("next_run"))}\nLast run: {WeeklySchedules.When(current.At("last_run.at"))} · {WeeklySchedules.ResultText(current.At("last_run.result"))}", "muted"));
        var conflicts = current.Objects("conflicts").Select(c => c.Str("name")).ToList();
        if (conflicts.Count > 0) editor.Children.Add(U.P($"Also scheduled for this time: {string.Join(", ", conflicts)}. Actions run in saved list order.", "muted"));
        var actions = U.Wrap(8, ActionButton("Save schedule", SaveSchedule, "primary"),
            ActionButton("Cancel changes", async () => { if (await Discard()) { ResetSchedule(); Draw(); } }));
        if (current != null)
        {
            actions.Children.Add(ActionButton("Run now", async () =>
            {
                if (!await Discard() || !await Studio.ConfirmDraft() || !await Shell.Overlay.Confirm("Run this schedule now?", $"Run “{current.Str("name")}” on the connected switch?", "Run now")) return;
                var result = await Send(SchedulePath + "/run", "POST", new JsonObject { ["expected_revision"] = current["revision"].Copy() });
                await ReloadScheduleAfterWrite(WeeklySchedules.ResultText(result.At("result")), result.Str("result") != "ok", refreshState: true);
            }));
            actions.Children.Add(ActionButton("Delete schedule", async () =>
            {
                if (!await Shell.Overlay.Confirm("Delete schedule?", $"Remove “{current.Str("name")}”?", "Delete")) return;
                await Send(SchedulePath, "DELETE", new JsonObject { ["expected_revision"] = current["revision"].Copy() });
                await ReloadScheduleAfterWrite("Schedule deleted.");
            }, "quiet danger"));
        }
        Body.Children.Add(Split(list, Editor(editor, actions)));
    }
    async Task SaveSchedule()
    {
        var candidate = (JsonObject)schedule.DeepClone(); candidate["name"] = schedule.Str("name")?.Trim();
        var error = WeeklySchedules.ScheduleDraftError(candidate);
        if (error.Length != 0) { Tell(error, true); return; }
        var conflicts = WeeklySchedules.ScheduleConflicts(candidate, schedules);
        if (conflicts.Count > 0 && !await Shell.Overlay.Confirm("Schedules share this time", $"“{candidate.Str("name")}” shares this time with {string.Join(", ", conflicts.Select(c => $"“{c.Str("name")}”"))}. Actions run in saved list order.", "Save schedule")) return;
        var result = await Send(scheduleId == null ? Endpoint : SchedulePath, scheduleId == null ? "POST" : "PATCH", candidate);
        scheduleId = result.Str("id");
        await ReloadScheduleAfterWrite("Schedule saved.");
    }
    async Task ReloadScheduleAfterWrite(string message, bool error = false, bool refreshState = false)
    {
        try { if (refreshState) await Workspace.Refresh(false); await Load(); Tell(message, error); }
        catch { Tell(message + " Use Refresh schedules to check the current state.", true); }
    }
}
