using System.Net.WebSockets;
using System.Text.Json;
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
    long streamRevision, fullStateRevision, nextRefresh, appliedRefresh, bandwidthReceivedAt;
    readonly Dictionary<string, long> frameRevisions = [];

    /// <param name="adminKey">The administration key of the gateway this program started, for that address only.</param>
    public Workspace(Uri baseUri, string? adminKey = null) => Api = new EeApi(baseUri, adminKey);

    public EeApi Api { get; }

    /// <summary>The current state from /api/state with the Ethernet ports from /api/ports under "ports".</summary>
    public JsonObject State { get; set; } = [];

    public JsonObject Session { get; set; } = [];

    public JsonArray Effects { get; set; } = [];

    /// <summary>/api/workspace: whether this workspace controls a real switch ("live").</summary>
    public JsonObject Options { get; set; } = new() { ["live"] = false };

    /// <summary>/api/workspace: true when this window reaches the workspace through private access.</summary>
    public bool Remote => Options.Bool("remote") == true;

    /// <summary>/api/about: the service's version and disclaimer. Empty for a service that does not report them.</summary>
    public JsonObject About { get; set; } = [];

    public string Disclaimer => About.Str("disclaimer") is { Length: > 0 } text ? text : "Unofficial community project. Not affiliated with Ubiquiti.";

    /// <summary>The live stream is interrupted (the browser workspace's "disconnected").</summary>
    public bool Disconnected { get; private set; }

    /// <summary>A switch is signed in and its workspace is open ("workspaceReady").</summary>
    public bool Ready { get; set; }

    public bool Closing { get; set; }

    public int Busy { get; private set; }

    public string? WorkspaceId => Session.Str("workspace_id");

    internal UplinkMeasurements Bandwidth { get; } = new();
    void ReceiveBandwidth() => Bandwidth.Receive(State, WorkspaceId, Disconnected, bandwidthReceivedAt);

    public bool Connected => State.Bool("connected") == true;

    /// <summary>The stream opened, closed, or the busy count changed.</summary>
    public event Action? ConnectionChanged;

    /// <summary>A "frame" message was merged into State.</summary>
    public event Action? Frame;

    /// <summary>A "state" message replaced State (ports kept).</summary>
    public event Action? StateMessage;

    /// <summary>A "fans" message replaced State["fans"].</summary>
    public event Action? FansChanged;

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
        var start = BeginRefresh();
        var send = WorkspaceRequest();
        var stateTask = ReadState();
        var portsTask = send("/api/ports", "GET", null);
        await Task.WhenAll(stateTask, portsTask);
        var (next, receivedAt) = await stateTask;
        ApplyRefresh(start, next, await portsTask, receivedAt, rebuild);

        async Task<(JsonObject, long)> ReadState()
        {
            var state = await send("/api/state", "GET", null).ConfigureAwait(false);
            return (state?.AsObject() ?? [], Environment.TickCount64);
        }
    }

    internal readonly record struct RefreshStart(string? Workspace, long StreamRevision, long Request);
    internal RefreshStart BeginRefresh() => new(WorkspaceId, streamRevision, ++nextRefresh);
    internal void ApplyRefresh(RefreshStart start, JsonObject next, JsonNode? ports, long receivedAt, bool rebuild)
    {
        if (start.Workspace != WorkspaceId || start.Request < appliedRefresh) return;
        appliedRefresh = start.Request;
        // A full state message supplies the entire configuration. Otherwise overlay just the
        // fields the stream delivered since this request began, keeping the HTTP configuration.
        if (fullStateRevision <= start.StreamRevision)
        {
            var reportedGeneration = next.Str("telemetry.generation");
            next["ports"] = ports?.DeepClone();
            foreach (var (field, revision) in frameRevisions)
                if (revision > start.StreamRevision) next[field] = State[field]?.DeepClone();
            if (frameRevisions.GetValueOrDefault("uplink_bandwidth") <= start.StreamRevision)
            {
                bandwidthReceivedAt = receivedAt;
                if (frameRevisions.GetValueOrDefault("telemetry") > start.StreamRevision
                    && next.Str("telemetry.generation") != reportedGeneration)
                    next["uplink_bandwidth"] = null;
            }
            State = next;
            if (frameRevisions.GetValueOrDefault("schedule_clock") <= start.StreamRevision)
                ScheduleClock.Receive(State["schedule_clock"]);
        }
        else if (frameRevisions.GetValueOrDefault("ports") <= start.StreamRevision)
        {
            // The state message carries no Ethernet port list.
            State["ports"] = ports?.DeepClone();
        }
        ReceiveBandwidth();
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
            StopFrames();
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
        var delivery = new StreamDelivery(this, stop);
        try
        {
            using var socket = new ClientWebSocket();
            await Api.ConnectStreamAsync(socket, token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => { if (socketStop == stop && !token.IsCancellationRequested) Opened(); });
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
                try
                {
                    if (JsonNode.Parse(message.GetBuffer().AsSpan(0, (int)message.Length)) is JsonObject parsed)
                        delivery.Receive(parsed);
                }
                catch (JsonException) { }
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
        ReceiveBandwidth();
        ConnectionChanged?.Invoke();
        StreamRestored?.Invoke();
    }

    void Closed(CancellationTokenSource stop)
    {
        var current = socketStop == stop;
        stop.Dispose();
        if (!current) return;
        socketStop = null;
        socketOpen = false;
        if (Closing || paused || lifetime.IsCancellationRequested) return;
        Disconnected = true;
        ReceiveBandwidth();
        ConnectionChanged?.Invoke();
        StreamLost?.Invoke();
        retry ??= new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) => { retry!.Stop(); ConnectFrames(); });
        retry.Start();
    }

    // Coalesce adjacent frame messages. A session or state change ends the batch so messages retain wire order.
    sealed class StreamDelivery(Workspace owner, CancellationTokenSource stream)
    {
        sealed class Delivery(JsonObject message, long receivedAt)
        {
            public JsonObject Message { get; } = message;
            public long ReceivedAt { get; set; } = receivedAt;
        }
        readonly object gate = new();
        Delivery? pending;

        public void Receive(JsonObject message)
        {
            var now = Environment.TickCount64;
            Delivery item;
            lock (gate)
            {
                var frame = message.Str("type") == "frame";
                if (frame && pending is not null)
                {
                    if (message.ContainsKey("uplink_bandwidth")) pending.ReceivedAt = now;
                    MergeFrame(pending.Message, message);
                    return;
                }
                item = new(message, now);
                pending = frame ? item : null;
            }
            Dispatcher.UIThread.Post(() =>
            {
                lock (gate) { if (ReferenceEquals(pending, item)) pending = null; }
                if (owner.socketStop == stream && !owner.Closing) owner.Handle(item.Message, item.ReceivedAt);
            }, DispatcherPriority.Background);
        }
    }

    static readonly string[] retainedFields = ["schedule_clock", "telemetry", "poe_summary", "ports", "sfp", "sfp_sync", "controller", "stats", "reconnect_required"];
    static readonly string[] nullableFields = ["identify", "uplink_bandwidth", "error"];

    // Frames own their JSON. Move incoming readings into place without copying entire port arrays.
    // The wire format distinguishes "no new reading" from explicitly clearing identify, bandwidth or an error.
    internal static void MergeFrame(JsonObject destination, JsonObject incoming, Dictionary<string, long>? revisions = null, long revision = 0)
    {
        foreach (var key in retainedFields)
            if (incoming[key] is not null) Move(key);
        foreach (var key in nullableFields)
            if (incoming.ContainsKey(key)) Move(key);
        if (incoming.ContainsKey("connected")) Move("connected");
        if (incoming.ContainsKey("mode")) Move("mode");

        void Move(string key)
        {
            var value = incoming[key];
            incoming.Remove(key);
            destination[key] = value;
            if (revisions is not null) revisions[key] = revision;
        }
    }

    internal void Handle(JsonObject message, long receivedAt)
    {
        switch (message.Str("type"))
        {
            case "frame" when Ready:
                ScheduleClock.Receive(message["schedule_clock"]);
                if (message.ContainsKey("connected")) Session["connected"] = message["connected"]?.DeepClone();
                if (!message.ContainsKey("uplink_bandwidth") && message["telemetry"] is JsonObject telemetry &&
                    telemetry.ContainsKey("generation") && State.Str("telemetry.generation") != telemetry.Str("generation"))
                    message["uplink_bandwidth"] = null;
                if (message.ContainsKey("uplink_bandwidth")) bandwidthReceivedAt = receivedAt;
                MergeFrame(State, message, frameRevisions, ++streamRevision);
                ReceiveBandwidth();
                ConnectionChanged?.Invoke();
                Frame?.Invoke();
                break;
            case "state" when Ready:
                if (message["state"] is not JsonObject next) break;
                ScheduleClock.Receive(next["schedule_clock"]);
                var replaced = (JsonObject)next.DeepClone();
                replaced["ports"] = State["ports"]?.DeepClone();
                State = replaced;
                fullStateRevision = ++streamRevision;
                bandwidthReceivedAt = receivedAt;
                ReceiveBandwidth();
                StateMessage?.Invoke();
                break;
            case "fans" when Ready:
                // Fan readings arrive every 5 seconds on their own, so lighting frames stay small.
                State["fans"] = message["fans"]?.DeepClone();
                FansChanged?.Invoke();
                break;
            case "session":
                if (message["session"] is JsonObject session && SessionMessage is { } handler)
                    _ = handler((JsonObject)session.DeepClone()).ContinueWith(task => DesktopLog.Error("session_error", task.Exception!),
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                break;
        }
    }

    public void StopFrames()
    {
        retry?.Stop();
        var stop = socketStop;
        socketStop = null;
        socketOpen = false;
        stop?.Cancel();
    }

    public bool StreamOpen => socketOpen;

    public async ValueTask DisposeAsync()
    {
        Closing = true;
        StopFrames();
        lifetime.Cancel();
        retry?.Stop();
        await Idle().ConfigureAwait(false);
        Api.Dispose();
    }
}
