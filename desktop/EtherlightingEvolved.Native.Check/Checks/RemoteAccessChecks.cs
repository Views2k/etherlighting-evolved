using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Pages.Connect;
using EtherlightingEvolved.Native.Pages.Settings;
using EtherlightingEvolved.Native.Shell;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>A desktop window that reaches the workspace through private access, where only listed routes are forwarded.</summary>
internal static class RemoteAccessChecks
{
    const string LocalOnly = "This action is available only on the computer running EE.";
    const string SavedOnHost = "Reconnect to the switch saved on the computer running EE.";
    const string NoHostLogin = "No switch login is saved on the computer running EE. Sign in to the switch on that computer. This page opens the workspace when the switch connects.";
    const string HostLoginUnavailable = "The saved login could not be opened. Sign in to the switch on the computer running EE.";
    const string HostLoginRejected = "The switch rejected the saved login. Sign in to the switch on the computer running EE.";
    const string HostKeyUntrusted = "Accept the switch’s SSH host key on the computer running EE, then connect again.";
    const string HostKeyChanged = "The switch identity has changed. Review its SSH host key on the computer running EE before reconnecting.";
    const string HandoffWarning = " The computer running EE has no saved switch login that can be opened, so you will need that computer to connect again.";
    const string Scope = "Devices on Tailscale can use lighting, effects, automation, profiles, schedules and port names, can control fans in the desktop app, and can reconnect to the saved switch. Switch login, UniFi Network setup and these settings are available only on the computer running EE.";

    // Stands in for the private gateway: it answers what the connection page and Settings read, refuses every other
    // route as the gateway does, and refuses the live stream.
    const string Gateway = """
        import http from 'node:http';
        import fs from 'node:fs';
        const port = Number(process.env.EE_PREVIEW_PORT);
        process.stdin.resume();
        const server = http.createServer((req, res) => {
          fs.appendFileSync('requests', req.method + ' ' + req.url + '\n');
          const reply = (code, body) => {res.writeHead(code, {'Content-Type': 'application/json'}); res.end(JSON.stringify(body));};
          const route = req.method + ' ' + req.url;
          if (route === 'GET /api/workspace') return reply(200, {live: true, remote: true});
          if (route === 'GET /api/effects') return reply(200, JSON.parse(fs.readFileSync('effects.json', 'utf8')));
          if (route === 'GET /api/session') return reply(200, JSON.parse(fs.readFileSync('session.json', 'utf8')));
          if (route === 'GET /api/state') return reply(200, {num_ports: 8, ports: [], sfp: [], mode: 'running', stats: {}});
          if (route === 'GET /api/last-session') return reply(200, {});
          if (route === 'GET /api/private-access')
            return reply(200, {available: true, enabled: true, ready: true, can_configure: false, can_close_workspace: false, url: null});
          if (route === 'POST /api/session/restore?resume=true') return reply(409, {detail: {code: 'no_saved_login', message: 'x'}});
          reply(403, {detail: {code: 'local_access_required', message: 'This action is available only on the computer running EE.'}});
        });
        server.on('upgrade', (_req, socket) => socket.destroy());
        server.listen(port, '127.0.0.1', () => console.log('Browser preview: http://127.0.0.1:' + port));
        """;

    public static void Contracts(Action<bool, string> check)
    {
        check(Connections.ConnectionError("local_access_required") == LocalOnly, "a request refused by private access names the computer running EE");
        static ApiException Refused(string code) => new("", code, 409, false, null);
        check(Connections.RemoteConnectionError(Refused("no_saved_login")) == NoHostLogin
              && Connections.RemoteConnectionError(Refused("saved_login_unavailable")) == HostLoginUnavailable
              && Connections.RemoteConnectionError(Refused("auth_failed")) == HostLoginRejected
              && Connections.RemoteConnectionError(JsonNode.Parse("""{"code":"authentication_failed"}""")) == HostLoginRejected
              && Connections.RemoteConnectionError(Refused("host_key_untrusted")) == HostKeyUntrusted
              && Connections.RemoteConnectionError(JsonNode.Parse("""{"code":"host_key_changed"}""")) == HostKeyChanged
              && Connections.RemoteConnectionError(Refused("unreachable")) == Connections.Messages["unreachable"],
            "a remote window is sent to the computer running EE for login problems, as the browser is");
        check(Connections.RemoteCanRestore(JsonNode.Parse("""{"has_saved_login":true}"""))
              && !Connections.RemoteCanRestore(JsonNode.Parse("""{"has_saved_login":false}"""))
              && !Connections.RemoteCanRestore(JsonNode.Parse("{}"))
              && !Connections.RemoteCanRestore(JsonNode.Parse("""{"has_saved_login":true,"saved_login_error":"x"}""")),
            "a remote window can restore only a saved login that opens");
    }

    public static void Exercise(string output, Action<bool, string> check)
    {
        Dispatcher.UIThread.VerifyAccess();
        AvaloniaSynchronizationContext.InstallIfNeeded();
        var directory = Directory.CreateDirectory(Path.Combine(output, "remote-" + Guid.NewGuid().ToString("N"))).FullName;
        var script = Path.Combine(directory, "gateway.mjs");
        File.WriteAllText(script, Gateway);
        // The shell refuses an empty effect catalog, so the stub serves one bundled effect.
        File.WriteAllText(Path.Combine(directory, "effects.json"), new JsonArray(EffectCatalog.Catalog[0]!.DeepClone()).ToJsonString());
        WriteSession(directory, new JsonObject { ["logged_in"] = false, ["has_saved_login"] = true });
        // demo: false because the stub reports live, as private access always does.
        var host = new ServiceHost("node", script, directory, packaged: false, demo: false, directory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var task = Run();
        if (!task.IsCompleted) Dispatcher.UIThread.MainLoop(deadline.Token);
        try
        {
            if (!task.IsCompleted) throw new TimeoutException("The remote access checks timed out.");
            task.GetAwaiter().GetResult();
        }
        finally { host.Kill(); }

        async Task Run()
        {
            try
            {
                var address = await host.StartAsync(deadline.Token);
                await using var workspace = new Workspace(address);
                var shell = new AppShell(workspace) { OpenWorkspaceAddress = _ => Task.CompletedTask };
                var window = new ShellWindow(shell);
                try
                {
                    window.Show();
                    await shell.Start();
                    var connect = shell.CurrentPage!;
                    check(workspace.Remote && shell.PageName == "Connect" && connect is ConnectPage, "a remote window opens the connection page");
                    // The private access panel has its own Connection options, which stay hidden on a remote window.
                    var shown = connect.GetLogicalDescendants().OfType<TextBlock>().Where(block => block.IsEffectivelyVisible).Select(block => block.Text).ToList();
                    bool Shown(string label) => Find(connect, label) is { IsEffectivelyVisible: true };
                    check(!new[] { "Switch address", "SSH username", "SSH password", "Save login on this PC", "Connection options" }.Any(shown.Contains)
                          && !Shown("Connect to switch") && !Shown("Forget saved login") && !Shown("Continue with demo")
                          && Shown("Connect with saved login") && Shown("Change workspace address") && shown.Contains(SavedOnHost),
                        "a remote connection page offers only the saved login on the host and Change workspace address");

                    Find(connect, "Connect with saved login")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => Texts(connect).Contains(NoHostLogin));
                    check(Requests(directory).Contains("POST /api/session/restore?resume=true") && Texts(connect).Contains(NoHostLogin),
                        "Connect with saved login sends a restore and reports that the host has no saved login");

                    workspace.Session = new JsonObject { ["logged_in"] = false, ["has_saved_login"] = false };
                    var none = PageRegistry.Create("Connect", shell)!;
                    try
                    {
                        check(Find(none, "Connect with saved login")?.IsVisible == false && Texts(none).Contains(NoHostLogin)
                              && Find(none, "Change workspace address")?.IsVisible == true,
                            "a remote connection page without a saved login on the host sends the user to that computer");
                    }
                    finally { none.Leave(); }

                    workspace.Session = new JsonObject { ["logged_in"] = false, ["has_saved_login"] = true, ["saved_login_error"] = "x" };
                    var unreadable = PageRegistry.Create("Connect", shell)!;
                    try { check(Texts(unreadable).Contains(HostLoginUnavailable), "a remote connection page reports a saved login the host cannot open"); }
                    finally { unreadable.Leave(); }

                    workspace.Session = new JsonObject
                    {
                        ["logged_in"] = false, ["has_saved_login"] = true, ["error"] = new JsonObject { ["code"] = "handoff_recovery_required" },
                    };
                    var blocked = PageRegistry.Create("Connect", shell)!;
                    try
                    {
                        check(Find(blocked, "Connect with saved login")?.IsEnabled == false && Find(blocked, "Change workspace address")?.IsEnabled == true,
                            "a remote connection page keeps Change workspace address while the handoff needs a review on the host");
                    }
                    finally { blocked.Leave(); }

                    await SettingsChecks(shell, directory, check);
                    check(!Requests(directory).Any(line => line.Split(' ')[1] is "/api/session/login" or "/api/session/forget" or "/api/session/handoff"
                                                           || line.Split(' ')[1].StartsWith("/api/controller", StringComparison.Ordinal)),
                        "a remote window sends no login, forget, handoff or UniFi Network request on its own");
                }
                finally
                {
                    shell.ResetWorkspace();
                    window.AllowClose = true;
                    window.Close();
                }
            }
            finally { deadline.Cancel(); }
        }
    }

    static async Task SettingsChecks(AppShell shell, string directory, Action<bool, string> check)
    {
        var signedIn = new JsonObject { ["logged_in"] = true, ["connected"] = true, ["simulated"] = false, ["has_saved_login"] = true };
        WriteSession(directory, signedIn);
        var settings = PageRegistry.Create("Settings", shell)!;
        try
        {
            await Until(() => Find(settings, "Handoff to UniFi") is not null && Texts(settings).Contains(Scope));
            var paneBody = settings.GetLogicalDescendants().OfType<StackPanel>().First(panel => panel.Children.OfType<PrivateAccessPanel>().Any());
            check(Find(settings, "Forget saved switch login") is null && Find(settings, "UniFi")?.IsVisible == false && !paneBody.Children[2].IsVisible
                  && !settings.GetLogicalDescendants().OfType<ControllerConnection>().Any(),
                "remote Settings leaves out Forget saved switch login and UniFi Network setup");
            check(Find(settings, "Handoff to UniFi") is not null && Texts(settings).Contains(Scope),
                "remote Settings keeps Handoff to UniFi and says what devices on Tailscale can use");
            check(Find(settings, "Open in browser") is null, "a window connected by address does not offer Open in browser");
        }
        finally { settings.Leave(); }

        signedIn["has_saved_login"] = false;
        WriteSession(directory, signedIn);
        settings = PageRegistry.Create("Settings", shell)!;
        try
        {
            await Until(() => Find(settings, "Handoff to UniFi") is not null);
            Find(settings, "Handoff to UniFi")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => shell.Overlay.HasDialog);
            var warned = Texts(shell.Overlay).Any(text => text.StartsWith("Stop EE lighting", StringComparison.Ordinal) && text.EndsWith(HandoffWarning, StringComparison.Ordinal));
            shell.Overlay.HandleEscape();
            await Until(() => !shell.Overlay.HasDialog);
            check(warned, "a remote handoff confirmation warns when the host has no saved login that opens");
        }
        finally { settings.Leave(); }
    }

    static void WriteSession(string directory, JsonObject session) =>
        File.WriteAllText(Path.Combine(directory, "session.json"), session.ToJsonString());

    static List<string> Requests(string directory)
    {
        var file = Path.Combine(directory, "requests");
        return File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
    }

    static List<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();

    static Button? Find(Control root, string label) =>
        root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, label));

    static async Task Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
    }
}
