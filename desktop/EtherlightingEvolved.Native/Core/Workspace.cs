using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Threading;

namespace EtherlightingEvolved.Native.Core;

/// <summary>
/// The workspace data the browser controller (preview/app.mjs) keeps at module level: state, session, effect
/// catalog and workspace options, the live stream, and the serialized change queue. Everything here runs on the UI
/// thread except the stream reader, which hands each message to the UI thread.
/// </summary>
public sealed class Workspace : IAsyncDisposable
{
    readonly CancellationTokenSource lifetime = new();
    Task operation = Task.CompletedTask;
    CancellationTokenSource? socketStop;
    DispatcherTimer? retry;
    bool paused, socketOpen;
    JsonObject? pendingFrame;

    public Workspace(Uri baseUri) => Api = new EeApi(baseUri);

    public EeApi Api { get; }

    /// <summary>The current state from /api/state with the Ethernet ports from /api/ports under "ports".</summary>
    public JsonObject State { get; set; } = [];

    public JsonObject Session { get; set; } = [];

    public JsonArray Effects { get; set; } = [];

    /// <summary>/api/workspace: whether this workspace controls a real switch ("live").</summary>
    public JsonObject Options { get; set; } = new() { ["live"] = false };

    /// <summary>The live stream is interrupted (the browser workspace's "disconnected").</summary>
    public bool Disconnected { get; private set; }

    /// <summary>A switch is signed in and its workspace is open ("workspaceReady").</summary>
    public bool Ready { get; set; }

    public bool Closing { get; set; }

    public int Busy { get; private set; }

    public string? WorkspaceId => Session.Str("workspace_id");

    public bool Connected => State.Bool("connected") == true;

    /// <summary>The stream opened, closed, or the busy count changed.</summary>
    public event Action? ConnectionChanged;

    /// <summary>A "frame" message was merged into State.</summary>
    public event Action? Frame;

    /// <summary>A "state" message replaced State (ports kept).</summary>
    public event Action? StateMessage;

    /// <summary>A "session" message arrived; the shell accepts it.</summary>
    public event Func<JsonObject, Task>? SessionMessage;

    /// <summary>State was read again by Refresh; the flag is the "rebuild" argument.</summary>
    public event Action<bool>? Refreshed;

    /// <summary>The stream was interrupted ("The workspace connection was interrupted.").</summary>
    public event Action? StreamLost;

    public event Action? StreamRestored;

    public Task<JsonNode?> Request(string path, string method = "GET", JsonNode? body = null) =>
        Api.RequestAsync(path, method, body, WorkspaceId);

    /// <summary>
    /// A request bound to the current switch: it fails if the connected switch changes, and refuses to apply a scene
    /// or resume while the switch is disconnected (port of workspaceRequest).
    /// </summary>
    public Func<string, string, JsonNode?, Task<JsonNode?>> WorkspaceRequest()
    {
        var workspace = WorkspaceId;
        return async (path, method, body) =>
        {
            if (workspace != WorkspaceId) throw new InvalidOperationException("The connected switch changed. Review its current setup.");
            if (method != "GET" && path is "/api/scene" or "/api/resume" && (Disconnected || !Connected))
                throw new InvalidOperationException("Reconnect the switch before applying. Your edits are preserved.");
            return await Api.RequestAsync(path, method, body, workspace);
        };
    }

    /// <summary>Runs changes one at a time, marking the workspace busy while each runs.</summary>
    public Task<T> Serialize<T>(Func<Task<T>> action)
    {
        var previous = operation;
        var task = Run();
        operation = task;
        return task;

        async Task<T> Run()
        {
            try { await previous; } catch (Exception) { }
            SetBusy(1);
            try { return await action(); }
            finally { SetBusy(-1); }
        }
    }

    public Task Serialize(Func<Task> action) => Serialize(async () => { await action(); return true; });

    /// <summary>Waits for queued changes to finish, ignoring their errors.</summary>
    public async Task Idle()
    {
        try { await operation; } catch (Exception) { }
    }

    void SetBusy(int change)
    {
        Busy += change;
        ConnectionChanged?.Invoke();
    }

    /// <summary>Reads state and ports again (port of refresh). Pages update from the Refreshed event.</summary>
    public async Task Refresh(bool rebuild = true)
    {
        var workspace = WorkspaceId;
        var send = WorkspaceRequest();
        var stateTask = send("/api/state", "GET", null);
        var portsTask = send("/api/ports", "GET", null);
        await Task.WhenAll(stateTask, portsTask);
        if (workspace != WorkspaceId) return;
        var next = (await stateTask)?.AsObject() ?? [];
        ScheduleClock.Receive(next["schedule_clock"]);
        next["ports"] = (await portsTask)?.DeepClone();
        State = next;
        ConnectionChanged?.Invoke();
        Refreshed?.Invoke(rebuild);
    }

    /// <summary>Opens the live stream (port of connectFrames). It reconnects 1.5 s after an interruption.</summary>
    public void ConnectFrames()
    {
        if (Closing || paused || socketStop is not null) return;
        retry?.Stop();
        var stop = socketStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = Task.Run(() => ReadStreamAsync(stop));
    }

    /// <summary>
    /// Stops the stream while the window is hidden or minimized, so EE uses no processor time drawing what nobody
    /// sees. Showing the window reads the state again and reconnects.
    /// </summary>
    public async Task SetPaused(bool value)
    {
        if (paused == value) return;
        paused = value;
        if (value)
        {
            retry?.Stop();
            socketStop?.Cancel();
            return;
        }
        ConnectFrames();
        if (Ready)
        {
            try { await Refresh(false); } catch (Exception) { }
        }
    }

    async Task ReadStreamAsync(CancellationTokenSource stop)
    {
        var token = stop.Token;
        try
        {
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", Api.BaseUri.GetLeftPart(UriPartial.Authority));
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            var address = new UriBuilder(Api.BaseUri) { Scheme = Api.BaseUri.Scheme == "https" ? "wss" : "ws", Path = "/api/ws" }.Uri;
            await socket.ConnectAsync(address, token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(Opened);
            var buffer = new byte[64 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > 8 * 1024 * 1024) throw new InvalidDataException("The workspace message is too large.");
                } while (!result.EndOfMessage);
                if (result.MessageType == WebSocketMessageType.Close) break;
                if (result.MessageType != WebSocketMessageType.Text) continue;
                if (Json.Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)) is JsonObject parsed) Receive(parsed);
            }
        }
        catch (Exception) when (!token.IsCancellationRequested) { }
        catch (OperationCanceledException) { }
        Dispatcher.UIThread.Post(() => Closed(stop));
    }

    void Opened()
    {
        socketOpen = true;
        Disconnected = false;
        ConnectionChanged?.Invoke();
        StreamRestored?.Invoke();
    }

    void Closed(CancellationTokenSource stop)
    {
        var intentional = stop.IsCancellationRequested;
        if (socketStop == stop) socketStop = null;
        stop.Dispose();
        socketOpen = false;
        if (Closing || paused || lifetime.IsCancellationRequested) return;
        Disconnected = true;
        ConnectionChanged?.Invoke();
        StreamLost?.Invoke();
        if (intentional) return;
        retry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => { retry!.Stop(); ConnectFrames(); });
        retry.Start();
    }

    // Frames arrive at the engine's rate. Consecutive frames are merged so the UI thread handles the newest values
    // once, with the same result as handling each in turn.
    void Receive(JsonObject message)
    {
        if (message.Str("type") == "frame")
        {
            lock (lifetime)
            {
                if (pendingFrame is not null)
                {
                    foreach (var (key, value) in message.ToList())
                    {
                        message.Remove(key);
                        pendingFrame[key] = value;
                    }
                    return;
                }
                pendingFrame = message;
            }
            Dispatcher.UIThread.Post(() =>
            {
                JsonObject frame;
                lock (lifetime) { frame = pendingFrame!; pendingFrame = null; }
                Handle(frame);
            }, DispatcherPriority.Background);
            return;
        }
        Dispatcher.UIThread.Post(() => Handle(message));
    }

    /// <summary>Applies one stream message (port of the socket message handler).</summary>
    public void Handle(JsonObject message)
    {
        switch (message.Str("type"))
        {
            case "frame" when Ready:
                ScheduleClock.Receive(message["schedule_clock"]);
                var state = State;
                void Take(string key, bool own = false)
                {
                    if (own ? message.ContainsKey(key) : message[key] is not null) state[key] = message[key]?.DeepClone();
                }
                foreach (var key in new[] { "schedule_clock", "telemetry", "poe_summary", "ports", "sfp", "sfp_sync", "controller", "stats", "reconnect_required" }) Take(key);
                foreach (var key in new[] { "identify", "uplink_bandwidth", "error" }) Take(key, own: true);
                state["connected"] = message["connected"]?.DeepClone();
                state["mode"] = message["mode"]?.DeepClone();
                Session["connected"] = message["connected"]?.DeepClone();
                ConnectionChanged?.Invoke();
                Frame?.Invoke();
                break;
            case "state" when Ready:
                if (message["state"] is not JsonObject next) break;
                ScheduleClock.Receive(next["schedule_clock"]);
                var replaced = (JsonObject)next.DeepClone();
                replaced["ports"] = State["ports"]?.DeepClone();
                State = replaced;
                StateMessage?.Invoke();
                break;
            case "session":
                if (message["session"] is JsonObject session && SessionMessage is { } handler) _ = handler((JsonObject)session.DeepClone());
                break;
        }
    }

    public void StopFrames()
    {
        retry?.Stop();
        socketStop?.Cancel();
    }

    public bool StreamOpen => socketOpen;

    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        retry?.Stop();
        await Idle().ConfigureAwait(false);
        Api.Dispose();
    }
}
