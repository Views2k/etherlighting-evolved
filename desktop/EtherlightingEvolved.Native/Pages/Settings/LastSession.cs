using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Port of preview/last-session.mjs: the Last session section, which shows how the previous EE session ended
/// (from /api/last-session) with a Refresh session details button.
/// </summary>
public sealed class LastSession : StackPanel
{
    static readonly Dictionary<string, string> Stages = new()
    {
        ["engine"] = "Lighting service", ["shutdown"] = "Workspace shutdown", ["parent_cleanup"] = "Desktop connection lost", ["desktop_exit"] = "Desktop shutdown",
    };

    static readonly Dictionary<string, string> Outcomes = new()
    {
        ["closed"] = "Cleanup confirmed", ["failed"] = "Service stopped with an error", ["cleanup_unconfirmed"] = "Lighting cleanup needs review",
    };

    readonly Send request;
    readonly StackPanel content = new();
    readonly Button refresh = SettingsKit.Button("Refresh session details", "quiet");
    bool active = true, pending;

    public LastSession(Send request)
    {
        this.request = request;
        Children.Add(SettingsKit.H2("Last session"));
        Children.Add(content);
        Children.Add(refresh);
        refresh.Click += async (_, _) => await Reload();
        _ = Reload();
    }

    public static (List<(string Label, string Value)>? Rows, string Message) View(JsonNode? payload)
    {
        var entry = payload.At("last_session");
        if (payload.Bool("available") != true) return (null, "Session details could not be read. Try Refresh.");
        if (!entry.Truthy()) return (null, "No earlier session has been recorded.");
        var stage = entry.Str("stage");
        var outcome = entry.Str("outcome");
        if (stage is null || !Stages.ContainsKey(stage) || outcome is null || !Outcomes.ContainsKey(outcome) || ParseTime(entry.Str("time")) is not { } time)
            return (null, "Session details could not be read. Try Refresh.");
        var rows = new List<(string, string)>
        {
            ("Time", LocaleString(time)), ("Stage", Stages[stage]), ("Result", Outcomes[outcome]),
            ("Version", entry.Str("build") is { Length: > 0 } build ? build : entry.At("build") is { } other && other.Truthy() ? other.ToJsonString() : "Unknown"),
        };
        if (entry.Num("attempts") is { } attempts && attempts == Math.Floor(attempts) && Math.Abs(attempts) <= 9007199254740991)
            rows.Add(("Cleanup attempts", attempts.ToString(CultureInfo.InvariantCulture)));
        return (rows, outcome == "closed" ? "" : "Review the connection status before resuming lighting.");
    }

    static DateTimeOffset? ParseTime(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out var time) ? time : null;

    /// <summary>Date.prototype.toLocaleString() in the en-US form: 11/11/2026, 9:33:59 PM.</summary>
    public static string LocaleString(DateTimeOffset time) =>
        time.ToLocalTime().ToString("M/d/yyyy, h:mm:ss tt", CultureInfo.InvariantCulture);

    void Render(JsonNode? payload)
    {
        var (rows, message) = View(payload);
        content.Children.Clear();
        if (rows is not null)
        {
            var details = new DetailsList();
            details.Set(rows);
            content.Children.Add(details);
        }
        if (message.Length > 0) content.Children.Add(SettingsKit.HelpText(message));
    }

    public async Task Reload()
    {
        if (!active || pending) return;
        pending = true;
        refresh.IsEnabled = false;
        try
        {
            var result = await request("/api/last-session");
            if (active) Render(result);
        }
        catch (Exception)
        {
            if (active) Render(new JsonObject { ["available"] = false });
        }
        finally
        {
            pending = false;
            if (active) refresh.IsEnabled = true;
        }
    }

    public void Close() => active = false;
}
