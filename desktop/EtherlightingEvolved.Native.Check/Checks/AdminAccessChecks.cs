using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Pages.Connect;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>The gateway's per-launch administration key: which requests carry it, the browser link, and the read-only panel.</summary>
internal static class AdminAccessChecks
{
    const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string HostOnly = "Change this setting in EE on the host computer.";

    public static void Contracts(Action<bool, string> check)
    {
        var local = new Uri("http://127.0.0.1:4180/");
        check(ServiceHost.BrowserLink(local, Key)?.AbsoluteUri == "http://127.0.0.1:4180/?admin_code=" + Key, "a browser link opens the workspace with the code");
        check(new[] { Key[1..], Key.ToUpperInvariant(), Key[..63] + "g", Key + "\n", null }.All(code => ServiceHost.BrowserLink(local, code) is null),
            "a browser link is made only from a 64-character lowercase hexadecimal code");
        check(Connections.PrivateAccessError("admin_required") == Connections.AdminRequired, "a change refused for the administration key names the app that started the workspace");
    }

    /// <summary>The key goes only to the administration paths, and only from a client given the key.</summary>
    public static async Task Transport(Action<bool, string> check)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var heads = new List<string>();
        using var stop = new CancellationTokenSource();
        var serving = Serve();
        var uri = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        try
        {
            using (var keyed = new EeApi(uri, Key))
            {
                check(!Carries(await Sent(() => keyed.GetAsync("/api/session"))), "an engine request never carries the administration key");
                check(Carries(await Sent(() => keyed.GetAsync("/api/private-access"))) && Carries(await Sent(() => keyed.RequestAsync("/api/workspace/close", "POST"))),
                    "private access and close requests carry the administration key");
            }
            using (var plain = new EeApi(uri))
                check(!Carries(await Sent(() => plain.GetAsync("/api/private-access"))) && !Carries(await Sent(() => plain.RequestAsync("/api/workspace/close", "POST"))),
                    "a client without the key sends none");
            await using (var workspace = new Workspace(uri))
                check(!Carries(await Sent(() => workspace.Request("/api/private-access"))), "a workspace opened by address sends no key");
            await using (var owned = new Workspace(uri, Key))
                check(Carries(await Sent(() => owned.Request("/api/private-access"))), "the workspace of the service this program started sends its key");
        }
        finally
        {
            stop.Cancel();
            listener.Stop();
            await serving;
        }

        async Task<string> Sent(Func<Task> send)
        {
            int before;
            lock (heads) before = heads.Count;
            await send();
            lock (heads) return heads.Count > before ? heads[^1] : "";
        }

        static bool Carries(string head) => head.Contains("\r\nX-EE-Admin-Key: " + Key + "\r\n", StringComparison.OrdinalIgnoreCase);

        async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception) when (stop.IsCancellationRequested) { return; }
                using (client)
                {
                    var stream = client.GetStream();
                    var buffer = new byte[16384];
                    var text = new StringBuilder();
                    while (!text.ToString().Contains("\r\n\r\n") && await stream.ReadAsync(buffer, stop.Token) is var read and > 0)
                        text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    var head = text.ToString().Split("\r\n\r\n")[0] + "\r\n";
                    lock (heads) heads.Add(head);
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"u8.ToArray(), stop.Token);
                }
            }
        }
    }

    /// <summary>The Mobile and remote access panel in a window connected to a local gateway it did not start.</summary>
    public static void Panel(Action<bool, string> check)
    {
        var locked = new JsonObject { ["available"] = true, ["enabled"] = true, ["ready"] = true, ["https_port"] = 8444, ["can_configure"] = false, ["admin_required"] = true };
        var requests = new List<string>();
        var (panel, window) = Show((path, method) => { requests.Add($"{method} {path}"); return locked.DeepClone(); });
        try
        {
            check(ReadOnly(panel) == Connections.AdminRequired && Toggle(panel).IsEnabled == false && Find(panel, "Apply setting")?.IsEnabled == false,
                "a local window without the key shows the administration help and keeps the setting read-only");
            Click(panel, "Refresh private access");
            check(requests.SequenceEqual(["GET /api/private-access", "GET /api/private-access"]), "Refresh in a read-only panel only reads the status");
        }
        finally { Close(panel, window); }

        var demo = new JsonObject { ["available"] = false, ["enabled"] = false, ["ready"] = false, ["can_configure"] = false, ["can_close_workspace"] = false, ["admin_required"] = true };
        (panel, window) = Show((_, _) => demo.DeepClone());
        try { check(ReadOnly(panel) == HostOnly, "the demo gateway keeps the general read-only help"); }
        finally { Close(panel, window); }

        // A live host without Tailscale where private access is off: nothing was checked, so nothing is available.
        var noTailscale = new JsonObject
        {
            ["available"] = false, ["enabled"] = false, ["ready"] = false, ["error"] = null, ["https_port"] = 8444, ["can_configure"] = false,
            ["can_close_workspace"] = false, ["admin_required"] = true,
        };
        (panel, window) = Show((_, _) => noTailscale.DeepClone());
        try { check(ReadOnly(panel) == Connections.AdminRequired, "a local window without the key on a host without Tailscale shows the administration help"); }
        finally { Close(panel, window); }

        requests.Clear();
        var configurable = new JsonObject { ["available"] = true, ["enabled"] = false, ["ready"] = false, ["https_port"] = 8444, ["can_configure"] = true };
        var lockedOff = new JsonObject { ["available"] = true, ["enabled"] = false, ["ready"] = false, ["https_port"] = 8444, ["can_configure"] = false, ["admin_required"] = true };
        (panel, window) = Show((path, method) =>
        {
            requests.Add($"{method} {path}");
            if (method == "PUT") throw new ApiException(Connections.AdminRequired, "admin_required", 403, false, null);
            return (requests.Count == 1 ? configurable : lockedOff).DeepClone();
        });
        try
        {
            Toggle(panel).IsChecked = true;
            Click(panel, "Apply setting");
            check(requests.SequenceEqual(["GET /api/private-access", "PUT /api/private-access", "GET /api/private-access"])
                  && ReadOnly(panel) == Connections.AdminRequired && Find(panel, "Apply setting")?.IsEnabled == false && panel.HasChanges,
                "a change refused for the key reads the status once, shows the help and keeps the draft");
        }
        finally { Close(panel, window); }
    }

    /// <summary>The real demo gateway that the desktop check started from this checkout.</summary>
    public static async Task Service(ServiceHost host, Uri address, Action<bool, string> check)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, params (string Name, string Value)[] headers)
        {
            using var request = new HttpRequestMessage(method, new Uri(address, path));
            foreach (var (name, value) in headers) request.Headers.Add(name, value);
            return await http.SendAsync(request);
        }
        async Task<JsonNode?> Read(HttpResponseMessage response)
        {
            using (response) return Json.Parse(await response.Content.ReadAsStringAsync());
        }
        static string[] Cookies(HttpResponseMessage response) => response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];

        var plain = await Read(await Send(HttpMethod.Get, "/api/private-access"));
        check(plain.Bool("admin_required") == true && plain.Bool("can_close_workspace") == false, "the gateway gives a request without the key no administration access");
        var keyed = await Read(await Send(HttpMethod.Get, "/api/private-access", ("X-EE-Admin-Key", host.AdminKey!)));
        check(keyed.Bool("admin_required") == false && keyed.Bool("can_close_workspace") == true, "the gateway accepts the key that this program gave it");
        using (var refused = await Send(HttpMethod.Post, "/api/workspace/close"))
            check(refused.StatusCode == HttpStatusCode.Forbidden && Json.Parse(await refused.Content.ReadAsStringAsync()).Str("detail.code") == "admin_required" && host.Running,
                "closing the workspace without the key is refused and the service keeps running");

        var port = address.Port;
        var link = await host.BrowserLinkAsync(CancellationToken.None);
        using (var opened = await Send(HttpMethod.Get, link.PathAndQuery))
        {
            var cookies = Cookies(opened);
            check(opened.StatusCode == HttpStatusCode.SeeOther && opened.Headers.Location?.OriginalString == "/" && cookies.Length == 2
                  && cookies.All(cookie => cookie.StartsWith($"ee_admin_{port}=", StringComparison.Ordinal) && cookie.Contains("HttpOnly") && cookie.Contains("SameSite=Strict"))
                  && cookies.Any(cookie => cookie.Contains("Path=/api/private-access;")) && cookies.Any(cookie => cookie.Contains("Path=/api/workspace/close;")),
                "a browser link sets the two path-limited cookies and returns to the workspace");
            using (var again = await Send(HttpMethod.Get, link.PathAndQuery))
                check(again.StatusCode == HttpStatusCode.SeeOther && Cookies(again).Length == 0, "a browser link works once");
            var cookie = cookies.FirstOrDefault()?.Split(';')[0] ?? "";
            var browser = await Read(await Send(HttpMethod.Get, "/api/private-access", ("Cookie", cookie)));
            check(browser.Bool("admin_required") == false, "the cookie from a browser link gives administration access");
        }

        var fresh = await host.BrowserLinkAsync(CancellationToken.None);
        using (var withOrigin = await Send(HttpMethod.Get, fresh.PathAndQuery, ("Origin", $"http://127.0.0.1:{port}")))
            check(Cookies(withOrigin).Length == 0, "a request with an Origin header does not use a browser link");
        using (var navigation = await Send(HttpMethod.Get, fresh.PathAndQuery))
            check(Cookies(navigation).Length == 2, "the same link still works for a browser navigation afterward");
    }

    static (PrivateAccessPanel Panel, Window Window) Show(Func<string, string, JsonNode?> answer)
    {
        var panel = new PrivateAccessPanel((path, method, _) =>
        {
            try { return Task.FromResult(answer(path, method)); }
            catch (Exception error) { return Task.FromException<JsonNode?>(error); }
        });
        var window = new Window { Width = 700, Height = 800, Content = new ScrollViewer { Content = panel } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (panel, window);
    }

    static void Close(PrivateAccessPanel panel, Window window)
    {
        panel.Close();
        window.Close();
    }

    // The read-only help sits directly in the panel's section; the notice below it can show the same text.
    static string? ReadOnly(Control panel) =>
        panel.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault(block => block.IsVisible && block.Parent is StackPanel parent
            && parent.Classes.Contains("private-access") && block.Text is HostOnly or Connections.AdminRequired)?.Text;

    static CheckBox Toggle(Control panel) => panel.GetLogicalDescendants().OfType<CheckBox>().First();

    static Button? Find(Control root, string label) => root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, label));

    static void Click(Control root, string label)
    {
        Find(root, label)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
    }
}
