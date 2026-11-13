using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Theme;
using U =EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Automation;

/// <summary>Port of openRuleTester (rule-tester.mjs).</summary>
internal sealed partial class AutomationPage
{
    async Task TestRules(JsonArray candidate)
    {
        var previous = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        var layout = Ports;
        var ids = layout.Copper.Concat(layout.Sfp).ToList();
        var port = U.Select(ids.Select(id => (id.ToString(CultureInfo.InvariantCulture), $"{(layout.Sfp.Contains(id) ? "SFP" : "Port")} {id}")).ToList());
        AutomationProperties.SetName(port, "Port");
        // datetime-local: a date and a 24-hour time, defaulting to the EE computer's clock
        var hostTime = ScheduleClock.HostDate(Workspace.State);
        var date = new CalendarDatePicker { SelectedDate = hostTime?.Date, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 34 };
        var time = U.Input(hostTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? "", "HH:mm");
        time.Width = 76;
        AutomationProperties.SetName(date, "Date");
        AutomationProperties.SetName(time, "Time");
        var when = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(time, 1);
        when.Children.Add(date); when.Children.Add(time);
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
        var dateField = U.Field($"Date and time · {ScheduleClock.Label(Workspace.State)}", when);
        Grid.SetColumn(dateField, 1);
        fields.Children.Add(U.Field("Port", port)); fields.Children.Add(dateField);
        var problem = U.Text("", "danger", true);
        problem.FontSize = 13;
        problem.MinHeight = 19;
        var results = new StackPanel();
        var output = new ScrollViewer { Content = results, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var title = U.Text("Test rules", "h2", true);
        title.FontSize = 18;
        title.Margin = new Thickness(0, 0, 0, 16);
        var help = HelpText("Evaluate these rule edits and their priority using the latest port readings.");
        help.Margin = new Thickness(0, 5, 0, 0);
        var actions = U.Row(8);
        var view = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*,Auto"), RowSpacing = 0 };
        problem.Margin = new Thickness(0, 24, 0, 0);
        output.Margin = new Thickness(0, 16, 0, 0);
        var footer = new Border { Child = actions, Padding = new Thickness(0, 12), Margin = new Thickness(0, 16, 0, 0) }.Bind(Border.BackgroundProperty, "dialog");
        Grid.SetRow(help, 1); Grid.SetRow(fields, 2); Grid.SetRow(problem, 3); Grid.SetRow(output, 4); Grid.SetRow(footer, 5);
        view.Children.Add(title); view.Children.Add(help); view.Children.Add(fields); view.Children.Add(problem); view.Children.Add(output); view.Children.Add(footer);

        // .rule-test-dialog: min(680px, 100vw - 32px) wide, the actions stay visible while the results scroll.
        var dialog = Shell.Overlay.Show(view, width: 680);
        if (dialog.Frame.Child is ScrollViewer wrapper) wrapper.Content = null;
        dialog.Frame.Child = view;
        dialog.Frame.Width = double.NaN;
        dialog.Frame.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (TopLevel.GetTopLevel(this) is { } top) dialog.Frame.MaxHeight = Math.Max(240, top.ClientSize.Height - 48);
        Button? test = null;
        async Task Evaluate()
        {
            if (!dialog.IsOpen || test?.IsEnabled == false) return;
            if (!int.TryParse(U.Value(port), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || date.SelectedDate is not { } day
                || !TimeOnly.TryParseExact(time.Text?.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
            {
                problem.Text = "Choose a port, date and time.";
                return;
            }
            test!.IsEnabled = false;
            problem.Text = "";
            try
            {
                var at = day.Date.Add(clock.ToTimeSpan()).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
                var result = await Send("/api/rules/test", "POST", new JsonObject { ["rules"] = candidate.Copy(), ["port"] = id, ["at"] = at });
                if (!dialog.IsOpen) return;
                results.Children.Clear();
                var source = result.Str("source") switch
                {
                    "individual_color" => "Individual port color",
                    "rule" => result.Str("winner.name") is { Length: > 0 } name ? name : "Rule",
                    "port_effect" => "Port effect",
                    "sfp_setting" => "SFP settings",
                    _ => "Unknown"
                };
                results.Children.Add(U.H3($"Displayed lighting: {source}"));
                if (result.Str("source") == "individual_color" && result.Truthy("winner"))
                    results.Children.Add(U.P($"Matching rule: {result.Str("winner.name")}. The individual port color takes priority.", "muted"));
                if (!result.Truthy("output_running")) results.Children.Add(U.P("EE output is currently stopped.", "muted"));
                int index = 0;
                foreach (var r in result.Objects("rules"))
                {
                    var verdict = !r.Truthy("enabled") ? "Disabled" : !r.Truthy("in_scope") ? "Outside selected ports" :
                        r.Truthy("matches") ? "Conditions match" : "Conditions do not match";
                    var name = U.Text($"{++index}. {r.Str("name")}", null, true);
                    name.FontWeight = FontWeight.Bold;
                    var row = new StackPanel { Spacing = 12 };
                    row.Children.Add(name);
                    row.Children.Add(U.P(verdict, "muted"));
                    foreach (var c in r.Objects("conditions"))
                    {
                        var line = U.Text($"{(c.Truthy("passes") ? "✓" : "–")} {c.Str("label")}: {TestValue(c, c["actual"], true)} · {OperatorNames.GetValueOrDefault(c.Str("op") ?? "") ?? c.Str("op")} {TestValue(c, c["value"])}", null, true);
                        line.FontSize = 14.4;
                        line.Margin = new Thickness(0, 8);
                        row.Children.Add(line);
                    }
                    results.Children.Add(new Border { Child = row, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 18, 0, 0) }.Bind(Border.BorderBrushProperty, "line"));
                }
            }
            catch (Exception e) { if (dialog.IsOpen) problem.Text = e.Message; }
            finally { if (dialog.IsOpen) test.IsEnabled = true; }
        }
        test = U.Button("Test rules", "primary", Evaluate);
        actions.Children.Add(test);
        actions.Children.Add(U.Button("Done", null, () => dialog.Close()));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => port.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
        try { await Evaluate(); } catch (Exception error) { problem.Text = error.Message; }
        await dialog.Result;
        if (Active && previous is { } control && control.GetVisualRoot() is not null) control.Focus();
    }

    /// <summary>conditionValue: a tested value in words, with the field's unit.</summary>
    static string TestValue(JsonObject condition, JsonNode? value, bool actual = false)
    {
        if (value is null || value.GetValueKind() == System.Text.Json.JsonValueKind.Null) return "Not reported";
        string? field = condition.Str("field");
        if (field == "time" && actual && value.Num() is { } minutes)
            return $"{Math.Floor(minutes / 60).ToString("00", CultureInfo.InvariantCulture)}:{(minutes % 60).ToString("00", CultureInfo.InvariantCulture)}";
        if (field == "weekday")
            return string.Join(", ", (value is JsonArray ? Values(value) : [Js.ToStr(value)]).Select(day => day.Length == 0 ? day : Title(day)));
        string text = value is JsonArray list ? string.Join(" to ", list.Select(item => Js.ToStr(item))) :
            value.Bool() is { } flag ? flag ? "Yes" : "No" : Js.ToStr(value);
        return text + (field is "speed" or "traffic" ? " Mbps" : field == "poe_power" ? " W" : "");
    }
}
