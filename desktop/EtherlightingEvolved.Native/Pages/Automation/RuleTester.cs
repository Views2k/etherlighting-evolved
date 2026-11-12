using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using U = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

internal sealed partial class AutomationPage
{
    async Task TestRules(JsonArray candidate)
    {
        var layout = Ports;
        var ids = layout.Copper.Concat(layout.Sfp).ToList();
        var port = U.Select(ids.Select(id => (id.ToString(), $"{(layout.Sfp.Contains(id) ? "SFP" : "Port")} {id}")).ToList());
        var hostTime = ScheduleClock.HostDate(Workspace.State);
        var date = new DatePicker { SelectedDate = hostTime is { } d ? new DateTimeOffset(d) : null };
        var time = new TimePicker { ClockIdentifier = "24HourClock", SelectedTime = hostTime?.TimeOfDay };
        var results = U.Column(10);
        var problem = U.P("", "error");
        var controls = U.Wrap(8);
        var view = U.Column(12, U.H2("Test rules"), U.P("Evaluate these rule edits and their priority using the latest port readings.", "muted"),
            U.Field("Port", port), U.Field($"Date and time · {ScheduleClock.Label(Workspace.State)}", U.Wrap(8, date, time)), problem, results, controls);
        var dialog = Shell.Overlay.Show(view, width: 580, sheet: true);
        Button? test = null;
        async Task Evaluate()
        {
            if (!dialog.IsOpen || test?.IsEnabled == false) return;
            if (!int.TryParse(U.Value(port), out var id) || date.SelectedDate == null || time.SelectedTime == null)
            { problem.Text = "Choose a port, date and time."; return; }
            test!.IsEnabled = false;
            try
            {
                var at = date.SelectedDate.Value.Date.Add(time.SelectedTime.Value).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
                var result = await Send("/api/rules/test", "POST", new JsonObject { ["port"] = id, ["at"] = at, ["rules"] = candidate.Copy() });
                if (!dialog.IsOpen) return;
                problem.Text = ""; results.Children.Clear();
                var source = result.Str("source") switch
                {
                    "individual_color" => "Individual port color",
                    "rule" => result.Str("winner.name") ?? "Rule",
                    "port_effect" => "Port effect",
                    "sfp_setting" => "SFP settings",
                    _ => "Unknown"
                };
                results.Children.Add(U.H3($"Displayed lighting: {source}"));
                if (result.Str("source") == "individual_color" && result.Str("winner.name") is { } winner)
                    results.Children.Add(U.P($"Matching rule: {winner}. The individual port color takes priority.", "muted"));
                if (result.Bool("output_running") == false) results.Children.Add(U.P("EE output is currently stopped.", "muted"));
                int index = 0;
                foreach (var r in result.Objects("rules"))
                {
                    var verdict = r.Bool("enabled") == false ? "Disabled" : r.Bool("in_scope") == false ? "Outside selected ports" :
                        r.Bool("matches") == true ? "Conditions match" : "Conditions do not match";
                    var row = U.Column(4, U.H3($"{++index}. {r.Str("name")}"), U.P(verdict, "muted"));
                    foreach (var c in r.Objects("conditions")) row.Children.Add(U.P($"{(c.Bool("passes") == true ? "✓" : "–")} {c.Str("label")}: {TestValue(c, "actual")} · {Rules.OperatorLabel(c.Str("op"))} {TestValue(c, "value")}", "muted"));
                    results.Children.Add(row);
                }
            }
            catch (Exception e) { if (dialog.IsOpen) problem.Text = e.Message; }
            finally { if (dialog.IsOpen) test.IsEnabled = true; }
        }
        test = U.Button("Test rules", "primary", Evaluate);
        controls.Children.Add(test); controls.Children.Add(U.Button("Done", null, () => dialog.Close()));
        await Evaluate();
        await dialog.Result;
    }
    static string TestValue(JsonObject condition, string prop)
    {
        var value = condition[prop]; if (value == null) return "Not reported";
        string? field = condition.Str("field");
        if (field == "time" && prop == "actual" && value.Int() is { } m) return $"{m / 60:00}:{m % 60:00}";
        if (field == "weekday") return value is JsonArray ? string.Join(", ", Values(value).Select(Title)) : Title(value.ToString());
        string text = value is JsonArray ? string.Join(" to ", Values(value)) :
            value.Bool() is { } b ? b ? "Yes" : "No" : value.ToString();
        return text + (field is "speed" or "traffic" ? " Mbps" : field == "poe_power" ? " W" : "");
    }
}
