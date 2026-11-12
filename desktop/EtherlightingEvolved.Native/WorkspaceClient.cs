using System.Net.WebSockets;
using System.Text.Json;

namespace EtherlightingEvolved.Native;

/// <summary>
/// Follows a running EE workspace: reads the session, state and ports, then listens to the live stream.
/// It only reads. It sends no commands, so it cannot change the switch.
/// </summary>
public sealed class WorkspaceClient : IAsyncDisposable
{
    readonly Uri baseUri;
    readonly HttpClient http;
    readonly CancellationTokenSource lifetime = new();
    readonly object gate = new();
    TaskCompletionSource resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenSource? stream;
    Task? runner;
    bool paused, signedIn = true, connected, loaded;
    string title = "UniFi switch", status = "Connecting to EE…";
    double gamma = 2.2;
    List<PortView> copper = [], sfp = [];
    LightingState lighting = LightingState.Empty;
    IReadOnlyList<EffectInfo> effects = [];
    long frames;

    public WorkspaceClient(Uri baseUri)
    {
        this.baseUri = baseUri;
        http = new HttpClient(WorkspaceTransport.CreateHandler(baseUri)) { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(10) };
        resumed.SetResult();
    }

    /// <summary>Raised on a background thread for every change worth showing.</summary>
    public event Action<WorkspaceSnapshot>? Changed;

    public long Frames => Interlocked.Read(ref frames);

    public string Address => baseUri.GetLeftPart(UriPartial.Authority);

    public void Start()
    {
        lock (gate) runner ??= Task.Run(() => RunAsync(lifetime.Token));
    }

    /// <summary>A minimized window shows nothing, so it disconnects from the stream until it is restored.</summary>
    public void SetPaused(bool value)
    {
        lock (gate)
        {
            if (paused == value) return;
            paused = value;
            if (value)
            {
                resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                stream?.Cancel();
            }
            else resumed.TrySetResult();
        }
        if (value) Publish("Paused while the window is minimized", false);
    }

    async Task RunAsync(CancellationToken token)
    {
        var delay = 1;
        while (!token.IsCancellationRequested)
        {
            Task wait;
            lock (gate) wait = resumed.Task;
            try { await wait.WaitAsync(token); }
            catch (OperationCanceledException) { return; }
            CancellationTokenSource current;
            lock (gate)
            {
                if (paused) continue;
                current = stream = CancellationTokenSource.CreateLinkedTokenSource(token);
            }
            try
            {
                await LoadAsync(current.Token);
                delay = 1;
                await StreamAsync(current.Token);
                if (!current.IsCancellationRequested) Fail("The EE workspace closed the live connection. Reconnecting…");
            }
            catch (Exception) when (current.IsCancellationRequested) { }
            catch (Exception)
            {
                Fail($"EE is not reachable at {Address}. Start Etherlighting Evolved; this window reconnects by itself.");
            }
            finally
            {
                lock (gate) if (stream == current) stream = null;
                current.Dispose();
            }
            bool isPaused;
            lock (gate) isPaused = paused;
            if (isPaused || token.IsCancellationRequested) continue;
            try { await Task.Delay(TimeSpan.FromSeconds(delay), token); }
            catch (OperationCanceledException) { return; }
            delay = Math.Min(delay * 2, 10);
        }
    }

    async Task LoadAsync(CancellationToken token)
    {
        using (var session = await GetAsync("/api/session", token))
            signedIn = WorkspaceModel.Bool(session.RootElement, "logged_in") == true;
        if (signedIn) await LoadStateAsync(token);
        else Publish();
    }

    async Task LoadStateAsync(CancellationToken token)
    {
        using var state = await GetAsync("/api/state", token);
        using var ports = await GetAsync("/api/ports", token);
        var root = state.RootElement;
        title = WorkspaceModel.Title(root);
        gamma = WorkspaceModel.Gamma(root);
        connected = WorkspaceModel.Bool(root, "connected") == true;
        copper = WorkspaceModel.Ports(ports.RootElement, default, gamma);
        sfp = WorkspaceModel.Ports(default, root.TryGetProperty("sfp", out var lights) ? lights : default, gamma);
        lighting = LightingModel.State(root, lighting);
        if (effects.Count == 0)
        {
            using var catalog = await GetAsync("/api/effects", token);
            effects = LightingModel.Effects(catalog.RootElement);
        }
        loaded = true;
        Publish();
    }

    async Task StreamAsync(CancellationToken token)
    {
        using var socket = new ClientWebSocket();
        await WorkspaceTransport.ConnectStreamAsync(socket, baseUri, http, token);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > 4 * 1024 * 1024) throw new InvalidDataException("The workspace message is too large.");
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            bool reload;
            using (var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length)))
                reload = Handle(document.RootElement);
            if (reload) await LoadStateAsync(token);
        }
    }

    /// <summary>Applies one stream message. Returns true when the full state must be read again.</summary>
    public bool Handle(JsonElement message)
    {
        switch (WorkspaceModel.Text(message, "type"))
        {
            case "frame":
                Interlocked.Increment(ref frames);
                if (message.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Array)
                    copper = WorkspaceModel.Ports(ports, default, gamma);
                if (message.TryGetProperty("sfp", out var lights) && lights.ValueKind == JsonValueKind.Array)
                {
                    sfp = WorkspaceModel.Ports(default, lights, gamma);
                    var settings = LightingModel.Sfp(lights);
                    if (!SameSfp(settings, lighting.Sfp)) lighting = lighting with { Sfp = settings };
                }
                if (WorkspaceModel.Text(message, "mode") is { } mode && (mode == "released") != lighting.Released)
                    lighting = lighting with { Released = mode == "released" };
                connected = WorkspaceModel.Bool(message, "connected") ?? connected;
                Publish();
                return false;
            case "state":
                if (!message.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.Object) return false;
                title = WorkspaceModel.Title(state);
                gamma = WorkspaceModel.Gamma(state);
                connected = WorkspaceModel.Bool(state, "connected") ?? connected;
                if (state.TryGetProperty("sfp", out var sfpState) && sfpState.ValueKind == JsonValueKind.Array)
                    sfp = WorkspaceModel.Ports(default, sfpState, gamma);
                lighting = LightingModel.State(state, lighting);
                Publish();
                return false;
            case "session":
                var nowSignedIn = WorkspaceModel.Bool(message, "session", "logged_in") == true;
                var reload = nowSignedIn && (!signedIn || !loaded);
                signedIn = nowSignedIn;
                if (!signedIn) { copper = []; sfp = []; loaded = false; lighting = LightingState.Empty; }
                Publish();
                return reload;
            default:
                return false;
        }
    }

    void Fail(string message)
    {
        connected = false;
        Publish(message, false);
    }

    void Publish(string? message = null, bool? live = null)
    {
        var isLive = live ?? (signedIn && connected);
        status = message ?? (!signedIn
            ? "EE is not signed in to a switch. Use Open full controls to sign in."
            : connected ? "Live" : "The switch connection was interrupted. EE is reconnecting.");
        var ports = new List<PortView>(copper.Count + sfp.Count);
        ports.AddRange(copper);
        foreach (var light in sfp)
            if (!copper.Exists(port => port.Port == light.Port)) ports.Add(light);
        Changed?.Invoke(new WorkspaceSnapshot(title, status, isLive, ports, lighting, effects));
    }

    static bool SameSfp(IReadOnlyDictionary<int, SfpSettings> a, IReadOnlyDictionary<int, SfpSettings> b) =>
        a.Count == b.Count && a.All(item => b.TryGetValue(item.Key, out var other) && other == item.Value);

    async Task<JsonDocument> GetAsync(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(token);
        return await JsonDocument.ParseAsync(body, cancellationToken: token);
    }

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        Task? running;
        lock (gate) { running = runner; resumed.TrySetResult(); }
        if (running is not null)
        {
            // ConfigureAwait(false): a caller on the UI thread may be waiting for this to finish.
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception) { }
        }
        http.Dispose();
        lifetime.Dispose();
    }
}
