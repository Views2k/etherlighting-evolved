using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;

namespace EtherlightingEvolved.Services;

public sealed class EngineApiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class EngineClient : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim mutations = new(1, 1);
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };
    private Process? process;
    private ClientWebSocket? socket;
    private Task? receiver;
    private string token = "";
    private bool disposed;
    private string? demoDirectory;

    public JsonObject State { get; private set; } = new();
    public JsonObject Session { get; private set; } = new();
    public JsonArray Effects { get; private set; } = new();
    public string? ConnectionError { get; private set; }
    public long FramesReceived { get; private set; }
    public bool IsStarted => !disposed && process is { HasExited: false } && http.BaseAddress is not null;
    public event Action? Changed;

    public async Task StartAsync(bool demo)
    {
        if (process is not null) throw new InvalidOperationException("The service is already started.");
        token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var start = LocateService();
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.Environment["PYTHONUNBUFFERED"] = "1";
        if (demo)
        {
            demoDirectory = Path.Combine(Path.GetTempPath(), "EtherlightingEvolved", "Preview", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(demoDirectory);
            start.Environment["EE_DATA_DIR"] = demoDirectory;
            foreach (var key in new[] { "EE_HOST", "EE_USERNAME", "EE_PASSWORD", "EE_KEY_FILE", "EE_API_TOKEN" })
                start.Environment.Remove(key);
        }
        process = Process.Start(start) ?? throw new EngineApiException("startup", "The lighting service could not start.");
        // Drain diagnostics without retaining potentially sensitive library output.
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { token, demo, data_dir = demoDirectory }));
            await process.StandardInput.FlushAsync();
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            startup.CancelAfter(TimeSpan.FromSeconds(30));
            var line = await process.StandardOutput.ReadLineAsync(startup.Token);
            var ready = line is null ? null : JsonNode.Parse(line);
            if (ready?["ready"]?.GetValue<bool>() != true || ready["port"] is null)
                throw new EngineApiException("startup", "The lighting service could not start. Close and reopen the app.");
            var port = ready["port"]!.GetValue<int>();
            if (port is < 1 or > 65535) throw new EngineApiException("startup", "The lighting service returned an invalid address.");
            http.BaseAddress = new Uri($"http://127.0.0.1:{port}/api/");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Effects = await GetAsync("effects") as JsonArray ?? new JsonArray();
            await RefreshAsync();
            receiver = ReceiveAsync(port);
        }
        catch
        {
            await StopProcessAsync();
            throw;
        }
    }

    public Task<JsonNode?> GetAsync(string path) => RequestAsync(HttpMethod.Get, path, null);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, object? body = null)
    {
        await mutations.WaitAsync(lifetime.Token);
        try
        {
            var result = await RequestAsync(method, path, body);
            await RefreshAsync();
            return result;
        }
        finally { mutations.Release(); }
    }

    public async Task RefreshAsync()
    {
        var session = await GetAsync("session") as JsonObject ?? new();
        JsonObject state = new();
        if (session["logged_in"]?.GetValue<bool>() == true)
        {
            try
            {
                state = await GetAsync("state") as JsonObject ?? new();
                state["ports"] = await GetAsync("ports");
            }
            catch (EngineApiException error) when (error.Code == "not_logged_in") { }
        }
        await PublishAsync(() =>
        {
            Session = session;
            State = state;
            ConnectionError = null;
        });
    }

    private async Task<JsonNode?> RequestAsync(HttpMethod method, string path, object? body)
    {
        if (http.BaseAddress is null) throw new EngineApiException("startup", "The lighting service is starting.");
        var relative = path.TrimStart('/');
        if (relative.StartsWith("api/", StringComparison.Ordinal)) relative = relative[4..];
        if (relative.Contains("://", StringComparison.Ordinal) || relative.StartsWith('/'))
            throw new ArgumentException("Only local API paths are accepted.", nameof(path));
        using var request = new HttpRequestMessage(method, relative);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, lifetime.Token); }
        catch (HttpRequestException) { throw new EngineApiException("service_unavailable", "The lighting service is unavailable. Close and reopen the app."); }
        catch (TaskCanceledException) when (!lifetime.IsCancellationRequested)
        {
            throw new EngineApiException("timeout", "The request timed out. Check the connection and try again.");
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(lifetime.Token);
            JsonNode? result = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
            if (!response.IsSuccessStatusCode)
            {
                var detail = result?["detail"] as JsonObject;
                var code = detail?["code"]?.GetValue<string>() ?? "request_failed";
                throw new EngineApiException(code, FriendlyError(code, response.StatusCode));
            }
            return result;
        }
    }

    private async Task ReceiveAsync(int port)
    {
        var delay = TimeSpan.FromMilliseconds(500);
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var connection = new ClientWebSocket();
                socket = connection;
                connection.Options.Proxy = null;
                connection.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                connection.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await connection.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/api/ws"), lifetime.Token);
                await PublishAsync(() => ConnectionError = null);
                delay = TimeSpan.FromMilliseconds(500);
                var buffer = new byte[16 * 1024];
                while (connection.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult chunk;
                    do
                    {
                        chunk = await connection.ReceiveAsync(new ArraySegment<byte>(buffer), lifetime.Token);
                        if (chunk.MessageType == WebSocketMessageType.Close) break;
                        message.Write(buffer, 0, chunk.Count);
                        if (message.Length > 1024 * 1024) throw new InvalidDataException("Unexpected service message size.");
                    } while (!chunk.EndOfMessage);
                    if (chunk.MessageType == WebSocketMessageType.Close) break;
                    if (chunk.MessageType != WebSocketMessageType.Text) continue;
                    var json = JsonNode.Parse(message.ToArray()) as JsonObject;
                    if (json is not null) await PublishAsync(() => ApplyMessage(json));
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception error) when (error is WebSocketException or IOException or JsonException or HttpRequestException) { }
            if (lifetime.IsCancellationRequested) break;
            await PublishAsync(() => ConnectionError = "Reconnecting to the lighting service…");
            try { await Task.Delay(delay, lifetime.Token); }
            catch (OperationCanceledException) { break; }
            delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 2));
        }
    }

    private void ApplyMessage(JsonObject message)
    {
        switch (message["type"]?.GetValue<string>())
        {
            case "session":
                Session = message["session"]?.DeepClone() as JsonObject ?? new();
                if (Session["logged_in"]?.GetValue<bool>() != true) State = new();
                break;
            case "state":
                var ports = State["ports"]?.DeepClone();
                State = message["state"]?.DeepClone() as JsonObject ?? new();
                if (ports is not null) State["ports"] = ports;
                break;
            case "frame":
                FramesReceived++;
                foreach (var key in new[] { "ports", "sfp", "connected", "mode" })
                    if (message.ContainsKey(key)) State[key] = message[key]?.DeepClone();
                break;
        }
    }

    private async Task PublishAsync(Action update)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
            await dispatcher.InvokeAsync(() => { update(); Changed?.Invoke(); });
        else { update(); Changed?.Invoke(); }
    }

    private static ProcessStartInfo LocateService()
    {
        var packaged = Path.Combine(AppContext.BaseDirectory, "engine", "EEEngine.exe");
        if (File.Exists(packaged)) return new ProcessStartInfo(packaged) { WorkingDirectory = Path.GetDirectoryName(packaged)! };
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var root = folder.FullName;
            var entry = Path.Combine(root, "packaging", "native_service.py");
            if (!File.Exists(entry)) continue;
            var python = Path.Combine(root, ".venv", "Scripts", "python.exe");
            if (!File.Exists(python)) throw new EngineApiException("missing_engine", "Build the lighting service before starting the app.");
            var result = new ProcessStartInfo(python) { WorkingDirectory = root };
            result.ArgumentList.Add(entry);
            result.Environment["PYTHONPATH"] = Path.Combine(root, "src");
            return result;
        }
        throw new EngineApiException("missing_engine", "The lighting service is missing. Reinstall the app.");
    }

    private static string FriendlyError(string code, HttpStatusCode status) => code switch
    {
        "missing_host" => "Enter your switch address.",
        "missing_username" => "Enter your SSH username.",
        "bad_port" => "Enter an SSH port from 1 to 65535.",
        "auth_failed" => "The SSH credentials were not accepted. Check your username and password.",
        "unreachable" => "The switch could not be reached. Check its address and network connection.",
        "host_key_changed" => "The switch identity changed. Verify the device before reconnecting.",
        "ssh_error" => "The SSH connection failed. Check the switch connection and try again.",
        "no_etherlighting" => "Per-port lighting is unavailable on this connection.",
        "not_logged_in" => "Connect to your switch to use this control.",
        "preview_only" => "Open the app normally to connect to a switch.",
        "output_unconfirmed" => "The switch has not confirmed this lighting change. Check its connection before retrying.",
        "bad_token" => "The app session expired. Close and reopen the app.",
        "invalid" => "Check the values and try again.",
        "not_found" => "This item is no longer available. Refresh and try again.",
        _ when status == HttpStatusCode.UnprocessableEntity => "Check the values and try again.",
        _ => "The change could not be completed. Try again."
    };

    private async Task StopProcessAsync()
    {
        var child = process;
        if (child is null || child.HasExited) return;
        try
        {
            await child.StandardInput.WriteLineAsync("quit");
            child.StandardInput.Close();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException) { }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try { await child.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            throw new EngineApiException("shutdown_pending", "The lighting service is still releasing control. Wait before opening another copy.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        lifetime.Cancel();
        socket?.Abort();
        if (receiver is not null)
        {
            try { await receiver; }
            catch (OperationCanceledException) { }
        }
        await StopProcessAsync();
        disposed = true;
        process?.Dispose();
        http.Dispose();
        lifetime.Dispose();
        token = "";
        // Preview data is isolated and may be retained for diagnosing UI tests.
    }
}
