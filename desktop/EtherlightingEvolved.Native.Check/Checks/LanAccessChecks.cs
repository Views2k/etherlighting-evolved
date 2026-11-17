using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Core.Model;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Pages.Connect;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;

namespace EtherlightingEvolved.Native.Check.Checks;

/// <summary>
/// LAN access in the desktop client: the address rule, the device key and the requests a LAN client may send, the
/// key store, the Settings panel on the computer running EE, and pairing from another computer. The checks never
/// listen on a LAN address: a LAN client's connections are sent to a loopback stand-in instead.
/// </summary>
internal static class LanAccessChecks
{
    const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    const string OtherKey = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";
    static readonly Uri LanAddress = new("http://192.168.1.2:4280/");

    // Private addresses other than the two documentation examples are built from parts, as the repository check requires.
    static string Address(params int[] parts) => string.Join('.', parts);

    public static void Contracts(Action<bool, string> check)
    {
        foreach (var text in new[] { "http://192.168.1.2:4280/", "http://10.0.0.1:4280", $"http://{Address(172, 16, 0, 1)}:4280", $"http://{Address(172, 31, 255, 254)}:4280" })
            check(WorkspaceAddress.Parse(text) is { } address && WorkspaceAddress.IsLan(address), $"{text} opens as a LAN address");
        foreach (var text in new[]
                 {
                     "http://192.168.1.2/", "http://192.168.1.2:80/", $"http://{Address(172, 32, 0, 1)}:4280", "http://100.100.1.1:4280", "http://169.254.1.1:4280",
                     "http://8.8.8.8:4280", "http://mypc.local:4280/", "http://[fd00::1]:4280/", "http://192.168.1.2:4280/path", "http://192.168.1.2:4280/?q=1",
                     "http://192.168.1.2:4280/#x", "http://user@192.168.1.2:4280/",
                 })
            check(WorkspaceAddress.Parse(text) is null, $"{text} is refused as a workspace address");
        check(WorkspaceAddress.Parse("https://ee.example.ts.net:8444/") is { } https && !WorkspaceAddress.IsLan(https)
              && WorkspaceAddress.Parse("http://127.0.0.1:4180/") is { } local && !WorkspaceAddress.IsLan(local),
            "the Tailscale HTTPS and local addresses open as before and are not LAN addresses");

        var answer = new JsonObject { ["live"] = true, ["remote"] = false };
        var lan = new Workspace(LanAddress) { Options = (JsonObject)answer.DeepClone() };
        var tailscale = new Workspace(new Uri("https://ee.example.ts.net:8444/")) { Options = (JsonObject)answer.DeepClone() };
        var here = new Workspace(new Uri("http://127.0.0.1:4180/")) { Options = (JsonObject)answer.DeepClone() };
        check(lan.Remote && lan.Lan && tailscale.Remote && !tailscale.Lan && !here.Remote && !here.Lan,
            "a window on another computer is remote, and a LAN window is LAN, whatever /api/workspace says");
        here.Options["remote"] = true;
        check(here.Remote, "a local address that the gateway reports as remote stays remote");
        foreach (var workspace in new[] { lan, tailscale, here }) workspace.DisposeAsync().AsTask().GetAwaiter().GetResult();

        check(EeApi.IsAdminPath("/api/lan-access") && EeApi.IsAdminPath("/api/lan-access/code") && EeApi.IsAdminPath("/api/lan-access/devices/3f9c0a1b2c3d4e5f"),
            "the LAN access settings, code and removal paths carry the administration key");
        check(new[] { "/api/lan-access/devices/3F9C0A1B2C3D4E5F", "/api/lan-access/devices/3f9c0a1b2c3d4e5", "/api/lan-access/devices/3f9c0a1b2c3d4e5f0",
                      "/api/lan-access/devices/..%2f..%2fworkspace", "/api/lan-access/devices/", "/api/lan-access/pair", "/api/lan-access?x=1" }.All(path => !EeApi.IsAdminPath(path)),
            "a device path with any id other than 16 lowercase hexadecimal characters does not carry the administration key");
        try
        {
            using var refused = new EeApi(LanAddress, Key);
            check(false, "a LAN client never carries the administration key");
        }
        catch (ArgumentException) { check(true, "a LAN client never carries the administration key"); }

        check(LanPairingDialog.PairingCode("482 913") == "482913" && LanPairingDialog.PairingCode("482-913") == "482913" && LanPairingDialog.PairingCode("48291") is null
              && LanPairingDialog.PairingCode("4829134") is null && LanPairingDialog.PairingCode("48291a") is null,
            "a pairing code is six digits, which may be grouped with spaces or hyphens");
        check(LanPairingDialog.DeviceName("  Office PC ") == "Office PC" && LanPairingDialog.DeviceName(new string('a', 40)) is not null
              && LanPairingDialog.DeviceName(new string('a', 41)) is null && LanPairingDialog.DeviceName("   ") is null
              && LanPairingDialog.DeviceName("Office​PC") is null && LanPairingDialog.DeviceName("Office\u0007PC") is null
              && LanPairingDialog.DefaultName.EnumerateRunes().Count() <= 40,
            "a device name has 1 to 40 characters without control or format characters, and the computer's name is offered");
        check(Connections.LanUnreachable(false) == "EE could not be reached at this address. LAN access may be off, or the computer running EE may have a new address. Check LAN access in Settings on that computer."
              && Connections.LanUnreachable(true).EndsWith(" On macOS 15 or later, also check that Etherlighting Evolved is allowed under System Settings > Privacy & Security > Local Network.", StringComparison.Ordinal),
            "a LAN workspace that cannot be reached is explained, with the Local Network permission on macOS");
        check(Connections.LanAccessError("lan_access_port_in_use") == "Another program uses this port. Choose another LAN port in Connection options."
              && Connections.LanAccessError(new ApiException("x", "admin_required", 403, false, null)) == Connections.LanAdminRequired
              && Connections.LanAccessError("unknown") == Connections.LanAccessFallback,
            "LAN access errors show the browser's messages and never the service's own text");
    }

    // The checks never listen on a LAN address, so a LAN client's connections go to a loopback stand-in.
    static void ThroughLoopback(EeApi api, int port)
    {
        var client = typeof(EeApi).GetField("http", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(api)!;
        var handler = (SocketsHttpHandler)typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
        handler.ConnectCallback = async (_, token) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    /// <summary>The device cookie on every request and on the stream, and nothing else sent before pairing.</summary>
    public static async Task Transport(Action<bool, string> check)
    {
        await using var stub = new Stub();
        stub.Answer = (head, _) => Stub.Line(head) switch
        {
            "POST /api/lan-access/pair" when stub.Count("POST /api/lan-access/pair") == 1 => Stub.Json(200, """{"paired":true}""", $"Set-Cookie: ee_lan_4280={Key}; Path=/api; Max-Age=31536000; HttpOnly; SameSite=Strict\r\n"),
            "POST /api/lan-access/pair" => Stub.Json(200, """{"paired":true}"""),
            "GET /api/redirect" => $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{stub.Port}/api/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
            _ => Stub.Json(200, "{}"),
        };
        static bool CarriesKey(string head) => head.Contains($"\r\nCookie: ee_lan_4280={Key}\r\n", StringComparison.OrdinalIgnoreCase);
        static bool CarriesCookie(string head) => head.Contains("\r\nCookie:", StringComparison.OrdinalIgnoreCase);

        using (var paired = new EeApi(LanAddress, deviceKey: Key))
        {
            ThroughLoopback(paired, stub.Port);
            await paired.GetAsync("/api/state");
            await paired.PutAsync("/api/scene", new JsonObject());
            await paired.GetAsync("/api/lan-access");
            check(stub.Heads.Count == 3 && stub.Heads.All(CarriesKey) && !stub.Heads.Any(head => head.Contains("X-EE-Admin-Key", StringComparison.OrdinalIgnoreCase)),
                "a paired LAN client sends its device cookie with every request and never the administration key");
            using (var socket = new ClientWebSocket())
            {
                await paired.ConnectStreamAsync(socket, CancellationToken.None);
                check(socket.State == WebSocketState.Open && stub.Heads[^1].StartsWith("GET /api/ws ", StringComparison.Ordinal) && CarriesKey(stub.Heads[^1]),
                    "a paired LAN client sends its device cookie when it opens the live stream");
            }
            var before = stub.Heads.Count;
            foreach (var (method, path) in new[] { ("POST", "/api/session/login"), ("POST", "/api/session/login?retry=1"), ("POST", "/api/controller/connect"),
                         ("POST", "/api/controller/consoles"), ("PUT", "/api/controller/preferences") })
            {
                try
                {
                    await paired.RequestAsync(path, method, new JsonObject { ["password"] = "fixture" });
                    check(false, $"{method} {path} is refused for a LAN address");
                }
                catch (ApiException error)
                {
                    check(error is { Code: "local_access_required", Status: 403, Message: "This action is available only on the computer running EE." }, $"{method} {path} is refused for a LAN address");
                }
            }
            check(stub.Heads.Count == before, "switch and UniFi passwords and keys are never sent to a LAN address");
            try
            {
                await paired.GetAsync("/api/redirect");
                check(false, "a LAN client does not follow redirects");
            }
            catch (ApiException error) { check(error.Status == 302 && stub.Count("GET /api/elsewhere") == 0, "a LAN client does not follow redirects"); }
        }

        using (var unpaired = new EeApi(LanAddress))
        {
            ThroughLoopback(unpaired, stub.Port);
            var before = stub.Heads.Count;
            await unpaired.GetAsync("/api/workspace");
            try
            {
                await unpaired.GetAsync("/api/state");
                check(false, "an unpaired LAN client sends only the workspace and pairing requests");
            }
            catch (ApiException error) { check(error.Code == "lan_pairing_required" && error.Status == 403 && stub.Heads.Count == before + 1, "an unpaired LAN client sends only the workspace and pairing requests"); }
            var key = await unpaired.PairAsync("482913", "Office PC");
            var body = JsonNode.Parse(stub.Bodies[^1]);
            check(key == Key && body.Str("code") == "482913" && body.Str("name") == "Office PC" && body.Str("client") == "app" && !CarriesCookie(stub.Heads[^1]),
                "pairing sends the code, the name and the app client, and reads the key from the cookie");
            try
            {
                await unpaired.PairAsync("482913", "Office PC");
                check(false, "an answer without the device cookie is refused");
            }
            catch (ApiException error) { check(error.Code == "invalid_response", "an answer without the device cookie is refused"); }
        }

        using (var local = new EeApi(new Uri($"http://127.0.0.1:{stub.Port}/"), deviceKey: Key))
        {
            await local.GetAsync("/api/state");
            var request = stub.Heads[^1];
            using var socket = new ClientWebSocket();
            await local.ConnectStreamAsync(socket, CancellationToken.None);
            check(!CarriesCookie(request) && stub.Heads[^1].StartsWith("GET /api/ws ", StringComparison.Ordinal) && !CarriesCookie(stub.Heads[^1]),
                "a client of a local address never sends a device cookie, on a request or on the stream");
        }

        // The LAN address over HTTPS. Nothing listens on the closed loopback port that stands in for it, so each request
        // is read just before it is sent and then fails to connect.
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var closedPort = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        using (var outgoing = new OutgoingRequests())
        using (var https = new EeApi(new Uri("https://192.168.1.2:4280/"), deviceKey: Key))
        {
            ThroughLoopback(https, closedPort);
            try { await https.GetAsync("/api/state"); }
            catch (ApiException) { }
            // The stream does not use the request handler for an HTTPS address, so a refused loopback proxy keeps it off the network.
            using (var socket = new ClientWebSocket())
            {
                socket.Options.Proxy = new WebProxy($"http://127.0.0.1:{closedPort}");
                try { await https.ConnectStreamAsync(socket, CancellationToken.None); }
                catch (WebSocketException) { }
            }
            var sent = outgoing.Sent.Where(item => item.Uri.Host == "192.168.1.2").ToList();
            check(sent.Select(item => item.Uri.AbsolutePath).SequenceEqual(["/api/state", "/api/ws"]) && !sent.Any(item => item.Cookie),
                "a client of an HTTPS address never sends a device cookie, on a request or on the stream");
        }
    }

    /// <summary>
    /// The requests this process sends, read from the runtime's HTTP diagnostics just before each is sent. TLS hides an
    /// HTTPS request on the wire, so the HTTPS check reads its headers here.
    /// </summary>
    sealed class OutgoingRequests : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        readonly List<IDisposable> subscriptions = [];
        readonly List<(Uri Uri, bool Cookie)> sent = [];

        public OutgoingRequests()
        {
            lock (subscriptions) subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        public List<(Uri Uri, bool Cookie)> Sent { get { lock (sent) return [.. sent]; } }

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "HttpHandlerDiagnosticListener") lock (subscriptions) subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> item)
        {
            // The event's payload type is internal; its Request property is the documented way to read the request.
            if (item.Key == "System.Net.Http.Request" && item.Value?.GetType().GetProperty("Request")?.GetValue(item.Value) is HttpRequestMessage { RequestUri: { } uri } request)
                lock (sent) sent.Add((uri, request.Headers.Contains("Cookie")));
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            lock (subscriptions) foreach (var subscription in subscriptions) subscription.Dispose();
        }
    }

    /// <summary>workspace-keys.json in the client data folder.</summary>
    public static void KeyStore(Action<bool, string> check)
    {
        System.IO.File.Delete(DeviceKeys.File);
        check(DeviceKeys.Get(LanAddress) is null, "no key is saved before pairing");
        DeviceKeys.Set(LanAddress, Key);
        DeviceKeys.Set(LanAddress, OtherKey);
        var saved = JsonNode.Parse(System.IO.File.ReadAllText(DeviceKeys.File));
        check(DeviceKeys.Get(new Uri("http://192.168.1.2:4280")) == OtherKey && saved.Int("format") == 1 && saved.Arr("keys").Count == 1
              && saved.Str("keys.0.origin") == "http://192.168.1.2:4280",
            "a new pairing replaces the key for its origin, in a format 1 file");
        check(DeviceKeys.Get(new Uri("http://192.168.1.2:4281/")) is null && DeviceKeys.Get(new Uri("http://127.0.0.1:4180/")) is null, "a key belongs to one LAN origin");
        try
        {
            DeviceKeys.Set(new Uri("https://ee.example.ts.net:8444/"), Key);
            check(false, "only a LAN address keeps a device key");
        }
        catch (ArgumentException) { check(true, "only a LAN address keeps a device key"); }
        for (var port = 4281; port <= 4296; port++) DeviceKeys.Set(new Uri($"http://192.168.1.2:{port}/"), Key);
        check(JsonNode.Parse(System.IO.File.ReadAllText(DeviceKeys.File)).Arr("keys").Count == 16 && DeviceKeys.Get(LanAddress) is null
              && DeviceKeys.Get(new Uri("http://192.168.1.2:4296/")) == Key,
            "at most 16 keys are kept, and the oldest gives way");
        if (!OperatingSystem.IsWindows())
            check(System.IO.File.GetUnixFileMode(DeviceKeys.File) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "only this user can read the saved keys");
        System.IO.File.WriteAllText(DeviceKeys.File, "{\"format\":1,\"keys\":[{\"origin\":\"http://192.168.1.2:4280\"}]}");
        check(DeviceKeys.Get(LanAddress) is null && System.IO.File.ReadAllText(DeviceKeys.File).Contains("4280", StringComparison.Ordinal),
            "a malformed key file reads as empty and is left as it is");
        System.IO.File.Delete(DeviceKeys.File);
    }

    /// <summary>The LAN access panel in Settings on the computer running EE, and its read-only forms.</summary>
    public static void Panel(Action<bool, string> check)
    {
        var off = new JsonObject
        {
            ["available"] = true, ["enabled"] = false, ["ready"] = false, ["port"] = 4280, ["addresses"] = new JsonArray(), ["error"] = null, ["pairing"] = null,
            ["devices"] = new JsonArray(), ["max_devices"] = 16, ["can_configure"] = true, ["can_change_port"] = true, ["admin_required"] = false,
        };
        var kitchen = new JsonObject
        {
            ["id"] = "3f9c0a1b2c3d4e5f", ["name"] = "Kitchen iPad", ["client"] = "browser", ["paired_at"] = "2026-11-16T20:10:12.000Z", ["paired_from"] = "10.0.0.1",
            ["last_used_at"] = null, ["last_address"] = null,
        };
        JsonObject On(params JsonObject[] devices)
        {
            var status = (JsonObject)off.DeepClone();
            status["enabled"] = true;
            status["ready"] = true;
            status["can_change_port"] = false;
            status["addresses"] = new JsonArray(new JsonObject { ["url"] = "http://192.168.1.2:4280", ["interface"] = "Ethernet" });
            status["devices"] = new JsonArray(devices.Select(device => (JsonNode)device.DeepClone()).ToArray());
            return status;
        }
        var current = (JsonObject)off.DeepClone();
        var requests = new List<string>();
        var questions = new List<string>();
        var accept = false;
        var (panel, window) = Show((path, method, body) =>
        {
            requests.Add($"{method} {path} {body?.ToJsonString()}".TrimEnd());
            if (method == "PUT") current = body.Bool("enabled") == true ? On() : (JsonObject)off.DeepClone();
            if (path == "/api/lan-access/code")
            {
                current["devices"] = new JsonArray(kitchen.DeepClone());
                current["pairing"] = new JsonObject
                {
                    ["code"] = "482913", ["expires_at"] = DateTime.UtcNow.AddSeconds(271.5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture), ["attempts_left"] = 5, ["ended"] = null,
                };
            }
            if (method == "DELETE") current["devices"] = new JsonArray();
            return current.DeepClone();
        }, question => { questions.Add(question); return accept; });
        try
        {
            check(Shown(panel, LanAccessPanel.Statement) && State(panel) == "LAN access is off" && Shown(panel, LanAccessPanel.Firewall)
                  && Find(panel, "Apply setting")?.IsEnabled == false && requests.SequenceEqual(["GET /api/lan-access"]),
                "the LAN panel shows the unencrypted statement and the off state, and Apply waits for a change");
            Toggle(panel).IsChecked = true;
            Click(panel, "Apply setting");
            check(requests[^1] == """PUT /api/lan-access {"enabled":true}""" && State(panel) == "LAN access is on" && Notice(panel) == "LAN access enabled."
                  && Shown(panel, "http://192.168.1.2:4280 (Ethernet)") && Find(panel, "Copy address")?.IsEffectivelyVisible == true,
                "turning LAN access on shows the address for other devices");
            Click(panel, "Pair a device");
            check(requests[^1] == "POST /api/lan-access/code" && Shown(panel, "482 913") && Shown(panel, "Open the address above on the other device and enter this code. It works once and expires in 4:32.")
                  && Find(panel, "New code")?.IsEffectivelyVisible == true && Find(panel, "Pair a device")?.IsEffectivelyVisible == false,
                "Pair a device shows the code and when it expires");
            check(Shown(panel, "Kitchen iPad") && panel.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text?.StartsWith("Browser · Paired ", StringComparison.Ordinal) == true
                      && block.Text.EndsWith(" from 10.0.0.1", StringComparison.Ordinal)),
                "the paired devices are listed with their client, pairing date and address");
            Toggle(panel).IsChecked = false;
            Click(panel, "Apply setting");
            check(questions.SequenceEqual(["Turn off LAN access?"]) && !requests.Any(line => line == """PUT /api/lan-access {"enabled":false}""") && panel.HasChanges,
                "turning LAN access off with devices paired asks first, and Cancel sends nothing");
            Click(panel, "Remove");
            check(requests[^1] == "DELETE /api/lan-access/devices/3f9c0a1b2c3d4e5f" && Notice(panel) == "Removed Kitchen iPad. It needs a new pairing code to connect again."
                  && !Shown(panel, "Kitchen iPad"),
                "Remove sends a DELETE for that device");
            current["devices"] = new JsonArray(kitchen.DeepClone());
            Click(panel, "Refresh LAN access");
            accept = true;
            Click(panel, "Apply setting");
            check(questions.Count == 2 && requests[^1] == """PUT /api/lan-access {"enabled":false}""" && State(panel) == "LAN access is off" && Notice(panel) == "LAN access disabled.",
                "accepting the question turns LAN access off");
        }
        finally { Close(panel, window); }

        var readOnly = (JsonObject)On().DeepClone();
        readOnly["can_configure"] = false;
        readOnly["admin_required"] = true;
        readOnly["devices"] = null;
        requests.Clear();
        (panel, window) = Show((path, method, _) => { requests.Add($"{method} {path}"); return readOnly.DeepClone(); }, _ => false);
        try
        {
            check(Shown(panel, Connections.LanAdminRequired) && Toggle(panel).IsEnabled == false && Find(panel, "Apply setting")?.IsEnabled == false
                  && Find(panel, "Pair a device")?.IsEffectivelyVisible == false && Shown(panel, LanAccessPanel.Statement),
                "a local window without the administration key shows LAN access read-only");
        }
        finally { Close(panel, window); }

        foreach (var (lan, lines) in new[] { (true, new[] { "LAN access", LanAccessPanel.LanWindow, LanAccessPanel.HostOnly }), (false, new[] { "LAN access", LanAccessPanel.HostOnly }) })
        {
            requests.Clear();
            var remote = new LanAccessPanel((path, method, _) => { requests.Add($"{method} {path}"); return Task.FromResult<JsonNode?>(null); }, (_, _, _) => Task.FromResult(false), remote: true, lan: lan);
            var shown = new Window { Width = 700, Height = 400, Content = remote };
            try
            {
                shown.Show();
                Dispatcher.UIThread.RunJobs();
                check(remote.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).SequenceEqual(lines) && requests.Count == 0,
                    lan ? "a LAN window says its own connection is not encrypted and where to change LAN access" : "a Tailscale window says where to change LAN access");
            }
            finally
            {
                remote.Close();
                shown.Close();
            }
        }
    }

    /// <summary>Pairing from another computer: the dialog, the saved key, a refusal, Cancel and an unreachable workspace.</summary>
    public static void Pairing(string output, Action<bool, string> check)
    {
        Dispatcher.UIThread.VerifyAccess();
        AvaloniaSynchronizationContext.InstallIfNeeded();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var task = Run();
        if (!task.IsCompleted) Dispatcher.UIThread.MainLoop(deadline.Token);
        if (!task.IsCompleted) throw new TimeoutException("The LAN pairing checks timed out.");
        task.GetAwaiter().GetResult();

        async Task Run()
        {
            try
            {
                System.IO.File.Delete(DeviceKeys.File);
                await using var stub = new Stub();
                // refuse: codes are wrong; accept: pairing works and the workspace then cannot be read; revoked: nothing is
                // paired; open: the workspace opens; removed: the workspace reports that this computer is no longer paired.
                // The stub closes each stream right after accepting it, as the gateway does for a removed device.
                var phase = "refuse";
                var catalog = EffectCatalog.Catalog.ToJsonString();
                stub.Answer = (head, _) => (Stub.Line(head), phase) switch
                {
                    ("POST /api/lan-access/pair", "refuse") => Stub.Json(403, """{"detail":{"code":"lan_pairing_failed","message":"x"}}"""),
                    ("POST /api/lan-access/pair", _) => Stub.Json(200, """{"paired":true}""", $"Set-Cookie: ee_lan_4280={Key}; Path=/api; Max-Age=31536000; HttpOnly; SameSite=Strict\r\n"),
                    (_, "accept") => Stub.Json(503, """{"detail":"The EE service is unavailable."}"""),
                    ("GET /api/effects", "open") => Stub.Json(200, catalog),
                    ("GET /api/workspace", "open") => Stub.Json(200, """{"live":true,"remote":true,"lan":true,"paired":true}"""),
                    ("GET /api/workspace", "removed") => Stub.Json(200, """{"live":true,"remote":true,"lan":true,"paired":false}"""),
                    (_, "open") => Stub.Json(200, "{}"),
                    _ => Stub.Json(403, """{"detail":{"code":"lan_pairing_required","message":"Pair this device first. Enter the pairing code shown on the computer running EE."}}"""),
                };

                await using (var workspace = new Workspace(LanAddress))
                {
                    ThroughLoopback(workspace.Api, stub.Port);
                    var (shell, window) = Open(workspace);
                    try
                    {
                        var starting = shell.Start();
                        await Until(() => shell.Overlay.HasDialog);
                        check(Texts(shell.Overlay).Contains(LanPairingDialog.Intro(LanAddress)) && Texts(shell.Overlay).Contains(LanPairingDialog.Statement)
                              && Texts(shell.Overlay).Contains(LanPairingDialog.Warning) && Texts(shell.Overlay).Contains("Pair with EE"),
                            "an unpaired LAN window asks for a pairing code with the unencrypted statement and the password warning");
                        var inputs = shell.Overlay.GetLogicalDescendants().OfType<TextBox>().ToList();
                        check(inputs.Count == 2 && inputs[1].Text == LanPairingDialog.DefaultName, "the device name defaults to the computer's name");
                        inputs[0].Text = "482 913";
                        inputs[1].Text = "Office PC";
                        Press(shell.Overlay, "Pair");
                        await Until(() => Texts(shell.Overlay).Any(text => text.StartsWith("The pairing code is wrong", StringComparison.Ordinal)));
                        check(Texts(shell.Overlay).Contains("The pairing code is wrong or has expired. Check the code on the computer running EE, or choose Pair a device there to show a new one.")
                              && shell.Overlay.HasDialog && DeviceKeys.Get(LanAddress) is null,
                            "a refused code is reported in the dialog");
                        var body = JsonNode.Parse(stub.Bodies[^1]);
                        check(body.Str("code") == "482913" && body.Str("name") == "Office PC" && body.Str("client") == "app", "the dialog sends the code, the name and the app client");
                        phase = "accept";
                        inputs[0].Text = "482913";
                        Press(shell.Overlay, "Pair");
                        await starting;
                        await Until(() => Texts(shell).Contains(Connections.LanUnreachable(OperatingSystem.IsMacOS())));
                        check(DeviceKeys.Get(LanAddress) == Key && workspace.Api.DeviceKey == Key && !shell.Overlay.HasDialog,
                            "a successful pairing saves the key for this address and gives it to the workspace");
                        check(Texts(shell).Contains(Connections.LanUnreachable(OperatingSystem.IsMacOS())) && Find(shell, "Try again") is not null,
                            "a LAN workspace that cannot be opened explains why, with the Local Network permission on macOS");
                    }
                    finally { Shut(shell, window); }
                }

                // The computer running EE removed this computer: its saved key is refused.
                phase = "revoked";
                await using (var workspace = new Workspace(LanAddress, deviceKey: DeviceKeys.Get(LanAddress)))
                {
                    ThroughLoopback(workspace.Api, stub.Port);
                    var (shell, window) = Open(workspace);
                    try
                    {
                        var starting = shell.Start();
                        await Until(() => shell.Overlay.HasDialog);
                        check(Texts(shell.Overlay).Contains(LanPairingDialog.RevokedIntro), "a saved key that is no longer accepted asks for a new pairing code");
                        Press(shell.Overlay, "Cancel");
                        await starting;
                        check(Texts(shell).Contains("Pair this computer with EE to open this workspace.") && Find(shell, "Pair") is not null
                              && Find(shell, "Change workspace address") is not null && DeviceKeys.Get(LanAddress) == Key,
                            "Cancel offers pairing again and Change workspace address, and keeps the saved key");
                    }
                    finally { Shut(shell, window); }
                }

                // The workspace opened, and then the computer running EE removed this computer while its stream was open.
                phase = "open";
                await using (var workspace = new Workspace(LanAddress, deviceKey: DeviceKeys.Get(LanAddress)))
                {
                    ThroughLoopback(workspace.Api, stub.Port);
                    var (shell, window) = Open(workspace);
                    try
                    {
                        await shell.Start();
                        await Until(() => stub.Count("GET /api/ws") > 0);
                        check(!shell.Overlay.HasDialog && stub.Count("GET /api/ws") > 0, "a paired LAN window opens the workspace and its stream");
                        phase = "removed";
                        await Until(() => shell.Overlay.HasDialog);
                        var streams = stub.Count("GET /api/ws");
                        check(Texts(shell.Overlay).Contains(LanPairingDialog.RevokedIntro) && DeviceKeys.Get(LanAddress) == Key,
                            "a LAN window whose stream closes after its pairing was removed asks for a new pairing code and keeps the saved key");
                        // The stream otherwise reconnects 1.5 seconds after it closes.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        check(stub.Count("GET /api/ws") == streams, "a LAN window whose pairing was removed stops reconnecting its stream");
                        Press(shell.Overlay, "Cancel");
                        await Until(() => Texts(shell).Contains("Pair this computer with EE to open this workspace."));
                        check(Texts(shell).Contains("Pair this computer with EE to open this workspace.") && DeviceKeys.Get(LanAddress) == Key,
                            "Cancel after a removed pairing offers pairing again and keeps the saved key");
                    }
                    finally { Shut(shell, window); }
                }
                System.IO.File.Delete(DeviceKeys.File);
            }
            finally { deadline.Cancel(); }
        }
    }

    /// <summary>The demo gateway that the desktop check started from this checkout.</summary>
    public static async Task Service(ServiceHost host, Uri address, Action<bool, string> check)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        using (var status = await http.GetAsync(new Uri(address, "/api/lan-access")))
        {
            var body = Json.Parse(await status.Content.ReadAsStringAsync());
            check(status.IsSuccessStatusCode && body.Bool("available") == false && body.Bool("enabled") == false && body.Bool("can_configure") == false,
                "the demo gateway reports LAN access unavailable");
        }
        using var change = new HttpRequestMessage(HttpMethod.Put, new Uri(address, "/api/lan-access")) { Content = new StringContent("""{"enabled":true}""", Encoding.UTF8, "application/json") };
        using var refused = await http.SendAsync(change);
        check(refused.StatusCode == HttpStatusCode.Forbidden && Json.Parse(await refused.Content.ReadAsStringAsync()).Str("detail.code") == "admin_required" && host.Running,
            "changing LAN access without the administration key is refused");
    }

    static (AppShell Shell, ShellWindow Window) Open(Workspace workspace)
    {
        var shell = new AppShell(workspace) { OpenWorkspaceAddress = _ => Task.CompletedTask };
        var window = new ShellWindow(shell);
        window.Show();
        return (shell, window);
    }

    static void Shut(AppShell shell, ShellWindow window)
    {
        shell.ResetWorkspace();
        window.AllowClose = true;
        window.Close();
    }

    static (LanAccessPanel Panel, Window Window) Show(Func<string, string, JsonNode?, JsonNode?> answer, Func<string, bool> confirm)
    {
        var panel = new LanAccessPanel((path, method, body) =>
        {
            try { return Task.FromResult(answer(path, method, body)); }
            catch (Exception error) { return Task.FromException<JsonNode?>(error); }
        }, (title, _, _) => Task.FromResult(confirm(title)));
        var window = new Window { Width = 760, Height = 1400, Content = new ScrollViewer { Content = panel } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (panel, window);
    }

    static void Close(LanAccessPanel panel, Window window)
    {
        panel.Close();
        window.Close();
    }

    static string State(Control panel) => panel.GetLogicalDescendants().OfType<StateLine>().First().Label;

    static string Notice(Control panel) => panel.GetLogicalDescendants().OfType<Notice>().First().Text;

    static CheckBox Toggle(Control panel) => panel.GetLogicalDescendants().OfType<CheckBox>().First();

    static bool Shown(Control root, string text) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.IsEffectivelyVisible && block.Text == text);

    static List<string> Texts(Control root) => root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text ?? "").ToList();

    static Button? Find(Control root, string label) => root.GetLogicalDescendants().OfType<Button>().FirstOrDefault(button => Equals(button.Content, label));

    static void Press(Control root, string label) => Find(root, label)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    static void Click(Control root, string label)
    {
        Press(root, label);
        Dispatcher.UIThread.RunJobs();
    }

    static async Task Until(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(25);
    }

    /// <summary>A loopback HTTP server that records each request and answers one request per connection.</summary>
    sealed class Stub : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly List<(string Head, string Body)> requests = [];
        readonly Task serving;

        public Stub()
        {
            listener.Start();
            serving = Serve();
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public Func<string, string, string> Answer { get; set; } = (_, _) => Json(200, "{}");

        public List<string> Heads { get { lock (requests) return requests.Select(item => item.Head).ToList(); } }

        public List<string> Bodies { get { lock (requests) return requests.Select(item => item.Body).ToList(); } }

        public int Count(string line) => Heads.Count(head => Line(head) == line);

        /// <summary>The method and path of a request head, without the query.</summary>
        public static string Line(string head) => string.Join(' ', head.Split("\r\n")[0].Split(' ').Take(2)).Split('?')[0];

        public static string Json(int status, string body, string headers = "") =>
            $"HTTP/1.1 {status} Answer\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n{headers}\r\n{body}";

        async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (Exception) when (stop.IsCancellationRequested) { return; }
                _ = Handle(client);
            }
        }

        async Task Handle(TcpClient client)
        {
            using (client)
            {
                try { await Exchange(client.GetStream()); }
                // A client that disconnects, or the stub stopping, ends this exchange only.
                catch (Exception error) when (error is IOException or OperationCanceledException) { }
            }
        }

        async Task Exchange(NetworkStream stream)
        {
            var received = new MemoryStream();
            var buffer = new byte[16384];
            int end;
            while ((end = HeadLength(received)) < 0)
            {
                var read = await stream.ReadAsync(buffer, stop.Token);
                if (read == 0) return;
                received.Write(buffer, 0, read);
            }
            var head = Encoding.ASCII.GetString(received.GetBuffer(), 0, end);
            var length = head.Split("\r\n").Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)).FirstOrDefault();
            while (received.Length - end < length)
            {
                var read = await stream.ReadAsync(buffer, stop.Token);
                if (read == 0) break;
                received.Write(buffer, 0, read);
            }
            var body = Encoding.UTF8.GetString(received.GetBuffer(), end, (int)received.Length - end);
            lock (requests) requests.Add((head, body));
            var answer = head.Contains("\r\nUpgrade: websocket", StringComparison.OrdinalIgnoreCase) ? Upgrade(head) : Answer(head, body);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(answer), stop.Token);
        }

        static int HeadLength(MemoryStream received)
        {
            var text = Encoding.ASCII.GetString(received.GetBuffer(), 0, (int)received.Length);
            var index = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return index < 0 ? -1 : index + 4;
        }

        static string Upgrade(string head)
        {
            var key = head.Split("\r\n").Select(line => line.Split(':', 2)).First(parts => parts[0].Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase))[1].Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            return $"HTTP/1.1 101 Switching Protocols\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            await serving;
        }
    }
}
