using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using EtherlightingEvolved.Services;

namespace EtherlightingEvolved.Views;

public sealed class SettingsView : UserControl
{
    private readonly EngineClient client;
    private readonly StackPanel content = new() { Width = 820, MaxWidth = 820, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock status = ViewUi.Status();
    private bool refreshing;

    public SettingsView(EngineClient client)
    {
        this.client = client;
        var root = new DockPanel { Margin = new Thickness(26, 20, 26, 20) };
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = root;
    }

    public async Task ReloadAsync()
    {
        if (refreshing) return;
        refreshing = true;
        try
        {
            var session = await client.GetAsync("/api/session");
            content.Children.Clear();
            content.Children.Add(ViewUi.Header("Settings", "Connection, saved login, and application details."));
            content.Children.Add(ViewUi.Text("Connection", 15));
            bool simulated = ViewUi.Bool(session, "simulated"), connected = ViewUi.Bool(session, "connected"), loggedIn = ViewUi.Bool(session, "logged_in");
            var state = ViewUi.Text(simulated ? "Demo switch" : connected ? "Switch connected" : loggedIn ? "Reconnecting to switch" : "Disconnected", 13, true);
            state.Margin = new Thickness(0, 8, 0, 16); content.Children.Add(state);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            if (loggedIn) actions.Children.Add(ViewUi.Button("Disconnect", DisconnectAsync));
            actions.Children.Add(ViewUi.Button("Refresh", ReloadAsync));
            content.Children.Add(actions);
            content.Children.Add(ViewUi.Separator());
            content.Children.Add(ViewUi.Text("Saved login", 15));
            bool saved = ViewUi.Bool(session, "has_saved_login");
            var savedStatus = ViewUi.Text(saved ? "Your login is saved for this Windows account." : "Save your login on the connection screen to reconnect on launch.", 13, true);
            savedStatus.Margin = new Thickness(0, 8, 0, 14); content.Children.Add(savedStatus);
            if (saved) content.Children.Add(ViewUi.Button("Forget saved login", ForgetAsync));
            if (session?["saved_login_error"] != null)
            {
                var error = ViewUi.Text("The saved login could not be read. Reconnect from the login screen to save it again.", 12, true); error.Margin = new Thickness(0, 10, 0, 0); content.Children.Add(error);
            }
            content.Children.Add(ViewUi.Separator());
            var wordmark = ViewUi.Text("Etherlighting Evolved", 21); wordmark.FontWeight = FontWeights.SemiBold; content.Children.Add(wordmark);
            var appVersion = typeof(SettingsView).Assembly.GetName().Version;
            var version = ViewUi.Text($"Version {appVersion?.Major}.{appVersion?.Minor}.{appVersion?.Build}", 12, true); version.Margin = new Thickness(0, 8, 0, 14); content.Children.Add(version);
            content.Children.Add(ViewUi.Text("Open-source UniFi Etherlighting controller.", 13, true));
            content.Children.Add(ViewUi.Text("Unofficial community project. Not affiliated with Ubiquiti.", 12, true));
        }
        catch (Exception ex) { ViewUi.Error(status, ex); }
        finally { refreshing = false; }
    }

    private async Task DisconnectAsync()
    {
        if (!ViewUi.Confirm(this, "Disconnect switch?", "Control returns to the switch. Your saved login and scenes are kept.", "Disconnect")) return;
        try { await client.SendAsync(HttpMethod.Post, "/api/session/logout"); await ReloadAsync(); status.Text = "Disconnected."; }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    private async Task ForgetAsync()
    {
        if (!ViewUi.Confirm(this, "Forget saved login?", "You will enter your login again next time. The current connection will stay active.", "Forget")) return;
        try { await client.SendAsync(HttpMethod.Post, "/api/session/forget"); await ReloadAsync(); status.Text = "Saved login removed."; }
        catch (Exception ex) { ViewUi.Error(status, ex); }
    }

    public async Task VerifyAsync()
    {
        ViewUi.RequireDemo(client);
        await ReloadAsync();
        ViewUi.Click(this, "Refresh");
        await ViewUi.WaitAsync(() => !refreshing);
        if (!ViewUi.Descendants<TextBlock>(this).Any(t => t.Text == "Demo switch")) throw new InvalidOperationException("The connection status did not render.");
        if (!ViewUi.Descendants<TextBlock>(this).Any(t => t.Text.Contains("Unofficial community project", StringComparison.Ordinal)))
            throw new InvalidOperationException("The application information did not render.");
    }
}
