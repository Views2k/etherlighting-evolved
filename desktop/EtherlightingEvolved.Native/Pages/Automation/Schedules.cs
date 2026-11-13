using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

/// <summary>Port of renderSchedules (weekly-schedules.mjs).</summary>
internal sealed partial class AutomationPage
{
    JsonArray schedules = [], profiles = [];
    JsonObject schedule = [];
    JsonNode? clock;
    string? scheduleId;
    JsonObject? Existing => schedules.Objects().FirstOrDefault(item => item.Str("id") == scheduleId);
    const string Endpoint = "/api/schedules";
    string SchedulePath => Endpoint + "/" + Uri.EscapeDataString(scheduleId!);

    async Task LoadSchedules()
    {
        var stored = Get(Endpoint); var saved = Get("/api/profiles");
        await Task.WhenAll(stored, saved);
        if (!Active || !schedulesTab) return;
        var result = await stored;
        schedules = result.Arr("schedules"); profiles = (await saved).Arr("profiles"); clock = result.At("clock");
        scheduleId = Existing?.Str("id") ?? schedules.Objects().FirstOrDefault()?.Str("id");
        ResetSchedule();
        loaded = true;
        Draw();
        bool review = result.Bool("history_available") == false;
        Tell(review ? "Schedule history needs review before schedules can run." : "", review);
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
        if (!await DiscardSchedule()) return;
        scheduleId = id; ResetSchedule(); Draw();
    }

    void DrawSchedules()
    {
        Body.Children.Add(Header("Automation", "Run your lighting setup on a weekly schedule.",
            ActionButton("Refresh schedules", async () => { if (await DiscardSchedule()) await LoadSchedules(); }),
            Key(ActionButton("New schedule", () => SelectSchedule(null), "primary"), "new-schedule")));
        var list = U.Column(0);
        AutomationProperties.SetName(list, "Weekly schedules");
        foreach (var row in schedules.Objects())
        {
            var id = row.Str("id") ?? "";
            var status = row.Truthy("needs_device_confirmation") ? "Review for connected switch" : row.Truthy("profile_missing")
                ? "Choose a saved profile" : row.Truthy("enabled") ? "Enabled" : "Disabled";
            list.Children.Add(Key(ListItem(ItemName(row.Str("name"), strong: true),
                [ItemLine($"{row.Str("at")} · {string.Join(", ", Values(row["days"]).Select(Title))}"), ItemLine(status)],
                id == scheduleId, () => Run(() => SelectSchedule(id))), "schedule:" + id));
        }
        if (schedules.Count == 0) list.Children.Add(U.P("Add a schedule to run a profile or change brightness at a chosen time.", "muted"));
        Body.Children.Add(Split(list, loaded ? ScheduleEditor() : LoadingPanel("Loading schedules…")));
    }

    Control ScheduleEditor()
    {
        var current = Existing;
        var editor = U.Column(18, U.H2(current == null ? "New schedule" : "Edit schedule"));
        var clockState = new JsonObject { ["schedule_clock"] = (clock ?? Workspace.State["schedule_clock"]).Copy() };
        var zone = HelpText($"Time on the EE computer · {ScheduleClock.Label(clockState)}");
        zone.Margin = new Thickness(0, 5, 0, 0);
        editor.Children.Add(zone);
        void Set(string key, JsonNode? value) { if (!JsonNode.DeepEquals(schedule[key], value)) { schedule[key] = value; edited = true; } }
        var name = Key(U.Input(schedule.Str("name"), changed: v => Set("name", v)), "schedule-name");
        name.MaxLength = 60;
        AutomationProperties.SetName(name, "Schedule name");
        var time = new TimePicker { ClockIdentifier = "24HourClock", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        if (TimeSpan.TryParseExact(schedule.Str("at"), @"hh\:mm", CultureInfo.InvariantCulture, out var start)) time.SelectedTime = start;
        time.SelectedTimeChanged += (_, _) => Set("at", time.SelectedTime?.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        AutomationProperties.SetName(time, "Time");
        var enabled = U.Check("Enabled", schedule.Truthy("enabled"), v => Set("enabled", v));
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 16, RowSpacing = 16 };
        var timeField = U.Field("Time", time);
        Grid.SetColumn(timeField, 1); Grid.SetRow(enabled, 1);
        title.Children.Add(U.Field("Schedule name", name)); title.Children.Add(timeField); title.Children.Add(enabled);
        editor.Children.Add(title);
        var dayControls = new WrapPanel { ItemSpacing = 12, LineSpacing = 12 };
        var selectedDays = Values(schedule["days"]).ToHashSet();
        foreach (var day in WeeklySchedules.Days) dayControls.Children.Add(U.Check(Title(day), selectedDays.Contains(day), on =>
        {
            if (on) selectedDays.Add(day); else selectedDays.Remove(day);
            Set("days", Strings(WeeklySchedules.Days.Where(selectedDays.Contains)));
        }));
        editor.Children.Add(U.Field("Days", dayControls));
        var action = schedule.Obj("action");
        var type = action.Str("type");
        var actionGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
        actionGrid.Children.Add(U.Field("Action", U.Select([("preset", "Apply profile"), ("brightness", "Set brightness"), ("release", "Hand off lighting to UniFi"), ("resume", "Resume EE lighting")],
            type, mode =>
            {
                if (mode == type) return;
                var value = new JsonObject { ["type"] = mode };
                if (mode == "preset") value["profile_id"] = ""; else if (mode == "brightness") value["value"] = 50;
                Set("action", value);
                Act(Draw);
            })));
        if (type == "preset")
        {
            var options = new List<(string, string)> { ("", "Choose a profile") };
            options.AddRange(profiles.Objects().Select(r => (r.Str("id") ?? "", r.Str("name") ?? "")));
            var profile = U.Field("Profile", U.Select(options, action.Str("profile_id"), v => { if (action.Str("profile_id") != v) { action["profile_id"] = v; edited = true; } }));
            Grid.SetColumn(profile, 1);
            actionGrid.Children.Add(profile);
        }
        if (type == "brightness")
        {
            var value = new NumericUpDown { Minimum = 0, Maximum = 100, Increment = 1, Value = action.Int("value") ?? 50, FormatString = "0" };
            value.ValueChanged += (_, _) => { if (value.Value != null) { action["value"] = (int)value.Value.Value; edited = true; } };
            var brightness = U.Field("Brightness · percent", value);
            Grid.SetColumn(brightness, 1);
            actionGrid.Children.Add(brightness);
        }
        editor.Children.Add(actionGrid);
        var info = U.Column(0, HelpText(current.Truthy("needs_device_confirmation") ? "Saving confirms this schedule for the connected switch." : "This schedule is saved for the connected switch."),
            U.P($"Next run: {WeeklySchedules.When(current.At("next_run"))}", "muted"),
            U.P($"Last run: {WeeklySchedules.When(current.At("last_run.at"))} · {WeeklySchedules.ResultText(current.At("last_run.result"))}", "muted"));
        var conflicts = current.Objects("conflicts").Select(c => c.Str("name")).ToList();
        if (conflicts.Count > 0) info.Children.Add(HelpText($"Also scheduled for this time: {string.Join(", ", conflicts)}. Actions run in saved list order."));
        editor.Children.Add(info);
        var actions = U.Wrap(8, ActionButton("Save schedule", SaveSchedule, "primary"),
            ActionButton("Cancel changes", async () => { if (await DiscardSchedule()) { ResetSchedule(); Draw(); } }, null));
        if (current != null)
        {
            actions.Children.Add(ActionButton("Run now", async () =>
            {
                if (!await DiscardSchedule() || !await Studio.ConfirmDraft() || !await Shell.Overlay.Confirm("Run this schedule now?", $"Run “{current.Str("name")}” on the connected switch?", "Run now")) return;
                var result = await Send(SchedulePath + "/run", "POST", new JsonObject { ["expected_revision"] = current["revision"].Copy() });
                await Workspace.Refresh(false);
                await LoadSchedules();
                Tell(WeeklySchedules.ResultText(result.At("result")), result.Str("result") != "ok");
            }, null));
            actions.Children.Add(ActionButton("Delete schedule", async () =>
            {
                if (!await Shell.Overlay.Confirm("Delete schedule?", $"Remove “{current.Str("name")}”?", "Delete")) return;
                await Send(SchedulePath, "DELETE", new JsonObject { ["expected_revision"] = current["revision"].Copy() });
                await LoadSchedules();
                Tell("Schedule deleted.");
            }, "quiet danger"));
        }
        return Editor(editor, actions);
    }

    async Task SaveSchedule()
    {
        var candidate = (JsonObject)schedule.DeepClone();
        candidate["name"] = schedule.Str("name")?.Trim() ?? "";
        // A profile that is no longer saved cannot be chosen in the browser's select, so it reads as no choice.
        if (candidate.Obj("action") is { } action && action.Str("type") == "preset"
            && !profiles.Objects().Any(profile => profile.Str("id") is { Length: > 0 } id && id == action.Str("profile_id")))
            action["profile_id"] = "";
        var error = WeeklySchedules.ScheduleDraftError(candidate);
        if (error.Length != 0) { Tell(error, true); return; }
        var conflicts = WeeklySchedules.ScheduleConflicts(candidate, schedules);
        if (conflicts.Count > 0 && !await Shell.Overlay.Confirm("Schedules share this time", $"“{candidate.Str("name")}” shares this time with {string.Join(", ", conflicts.Select(c => $"“{c.Str("name")}”"))}. Actions run in saved list order.", "Save schedule")) return;
        var result = await Send(scheduleId == null ? Endpoint : SchedulePath, scheduleId == null ? "POST" : "PATCH", candidate);
        scheduleId = result.Str("id");
        await LoadSchedules();
        Tell("Schedule saved.");
    }
}
