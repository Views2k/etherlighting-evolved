using System.Text.Json.Nodes;
using Avalonia.Controls;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Port of preview/last-session.mjs: the Last session section, which shows how the previous EE session ended
/// (from /api/last-session) with a Refresh session details button.
/// </summary>
public sealed class LastSession : StackPanel
{
    readonly Send request;
    readonly StackPanel content = new();
    readonly Button refresh = UiKit.Button("Refresh session details", "quiet");
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

    void Render(JsonNode? payload)
    {
        var view = Core.Model.LastSession.LastSessionView(payload);
        content.Children.Clear();
        if (view["rows"] is JsonArray rows)
        {
            var details = new DetailsList();
            details.Set(rows.Select(row => (row.Str("0") ?? "", row.Str("1") ?? "")));
            content.Children.Add(details);
        }
        if (view.Str("message") is { Length: > 0 } message) content.Children.Add(UiKit.HelpText(message));
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
