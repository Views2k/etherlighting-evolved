using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Pages;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Shell;

/// <summary>
/// The workspace frame and controller, ported from preview/app.mjs: title bar, navigation, status bar, the
/// reconnect and pending-preview bars, toasts, and the session flow (start, accept a session, open or reset a
/// workspace). The desktop releases the switch through Handoff to UniFi or Quit, not the browser's close button.
/// </summary>
public sealed class AppShell : UserControl
{
    public static readonly string[] PageNames = ["Lighting", "Effects", "Automation", "Profiles", "Fans", "Settings", "Help"];
    static readonly (string Name, string Icon)[] Navigation = [("Lighting", "grid"), ("Effects", "wave"), ("Automation", "activity"), ("Profiles", "layers"), ("Fans", "wind")];

    readonly Dictionary<string, Button> pageButtons = new(StringComparer.Ordinal);
    readonly Border view = new() { ClipToBounds = true };
    readonly Panel canvas = new();
    readonly TextBlock status = new() { Text = "Opening workspace", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly StateLine connection = StatusLine("Connecting…");
    readonly StateLine controller = StatusLine("Controller not connected");
    readonly Border recovery = new() { IsVisible = false, Padding = new Thickness(20, 12) };
    readonly Border pending = new() { IsVisible = false, Padding = new Thickness(18, 6), BorderThickness = new Thickness(0, 0, 0, 1) };
    readonly TextBlock pendingText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
    readonly Border titlebar = new() { MinHeight = 40, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(14, 0) };
    readonly Button reconnect;
    Page? current;
    JsonObject? openingWorkspace;
    Task? opening;
    int navigationVersion;
    readonly List<string> history = [];
    int historyIndex = -1;
    bool startedOnce, serviceStopped, pairingShown;

    public AppShell(Workspace workspace)
    {
        Workspace = workspace;
        Studio = PageRegistry.StudioFactory?.Invoke(this) ?? throw new InvalidOperationException("The Lighting editor is missing.");
        Overlay = new Overlay();
        reconnect = UiKit.Button("Reconnect switch");
        reconnect.Click += async (_, _) => await Run(async () =>
        {
            reconnect.IsEnabled = false;
            try
            {
                await Workspace.Request("/api/session/recover", "POST");
                await Workspace.Refresh(false);
                Notify("Switch connection restored");
            }
            finally { reconnect.IsEnabled = true; }
        });
        Content = Build();
        Workspace.ConnectionChanged += UpdateConnection;
        Workspace.Refreshed += OnRefreshed;
        Workspace.StateMessage += OnStateMessage;
        Workspace.Frame += OnFrame;
        Workspace.FansChanged += () => { if (!Hidden && PageName == "Fans") current?.Frame(); };
        Workspace.SessionMessage += AcceptSession;
        Workspace.StreamLost += () =>
        {
            UpdatePage();
            Status("The workspace connection was interrupted.", true);
            if (Workspace.Lan) _ = CheckPairing();
        };
        Workspace.StreamRestored += () =>
        {
            if (status.Text == "The workspace connection was interrupted.") Status("Workspace connection restored");
            UpdateDraftBar();
        };
    }

    public Workspace Workspace { get; }

    public IStudio Studio { get; }

    public Overlay Overlay { get; }

    /// <summary>The page shown ("page" in the browser workspace).</summary>
    public string PageName { get; private set; } = "Lighting";

    public Page? CurrentPage => current;

    // Desktop-only clients can choose the EE host. The bundled local workspace manages its own service.
    public Func<Uri, Task>? OpenWorkspaceAddress { get; init; }

    /// <summary>Opens this workspace in the default browser with administration access. Set only for the service this program started on Windows.</summary>
    public Func<Task>? OpenInBrowser { get; init; }

    internal async Task ChooseWorkspaceAddress()
    {
        if (OpenWorkspaceAddress is null || current is not null && !await current.CanLeave()) return;
        if (Studio.HasPendingPlan && !await Studio.ConfirmDraft()) return;
        var input = UiKit.Input(Workspace.Api.BaseUri.AbsoluteUri);
        var error = UiKit.Text("", "error", wrap: true);
        var actions = UiKit.Row(8);
        var content = UiKit.Column(14, UiKit.H2("Connect to a workspace"), UiKit.Field("Workspace address", input), error, actions);
        var dialog = Overlay.Show(content, width: 440);
        actions.Children.Add(UiKit.Button("Cancel", null, () => dialog.Close()));
        var connect = UiKit.Button("Connect", "primary");
        actions.Children.Add(connect);
        connect.Click += (_, _) =>
        {
            if (WorkspaceAddress.Parse(input.Text) is not null) dialog.Close("connect");
            else error.Text = AddressHelp;
        };
        input.Focus();
        if (await dialog.Result == "connect" && WorkspaceAddress.Parse(input.Text) is { } address)
            await Run(() => OpenWorkspaceAddress(address));
    }

    public const string AddressHelp = "Enter the Tailscale private HTTPS address, the LAN address shown in LAN access on the computer running EE, such as http://192.168.1.2:4280, or a local address.";

    /// <summary>The window asks to hide to the notification area (traffic-light close and minimize).</summary>
    public event Action? HideRequested;

    public event Action? ToggleMaximizeRequested;

    public event Action<PointerPressedEventArgs>? DragRequested;

    Control Build()
    {
        // Title bar: traffic lights and wordmark, centered navigation, Help, Settings and the logo.
        titlebar.Bind(Border.BackgroundProperty, "chrome").Bind(Border.BorderBrushProperty, "line");
        var lights = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var lightButtons = new[]
        {
            new TrafficLight(TrafficLight.Kind.Close, "Hide to notification area", () => HideRequested?.Invoke()),
            new TrafficLight(TrafficLight.Kind.Minimize, "Minimize to tray", () => HideRequested?.Invoke()),
            new TrafficLight(TrafficLight.Kind.Maximize, "Maximize", () => ToggleMaximizeRequested?.Invoke()),
        };
        foreach (var light in lightButtons) lights.Children.Add(light);
        lights.PointerEntered += (_, _) => { foreach (var light in lightButtons) light.ShowGlyph = true; };
        lights.PointerExited += (_, _) => { foreach (var light in lightButtons) light.ShowGlyph = false; };
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(lights);
        var brandName = new TextBlock { Text = "Etherlighting Evolved", FontSize = 14, FontWeight = FontWeight.SemiBold, LetterSpacing = -0.15, VerticalAlignment = VerticalAlignment.Center }.Bind(TextBlock.ForegroundProperty, "text");
        brand.Children.Add(brandName);

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (name, icon) in Navigation)
        {
            var button = NavButton(name, icon, "nav");
            pageButtons[name] = button;
            nav.Children.Add(button);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (name, icon) in new[] { ("Help", "info"), ("Settings", "settings") })
        {
            var button = NavButton(name, icon, "window-action");
            pageButtons[name] = button;
            actions.Children.Add(button);
        }
        var logo = new Image { Width = 30, Height = 30, Margin = new Thickness(6, 0, 0, 0), Stretch = Stretch.Uniform };
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://EtherlightingEvolved/Assets/logo-256.png"));
            logo.Source = Bitmap.DecodeToWidth(stream, 60, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception) { }
        ToolTip.SetTip(logo, "Etherlighting Evolved");
        actions.Children.Add(logo);
        var bar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 14, Background = Brushes.Transparent };
        bar.Children.Add(brand);
        Grid.SetColumn(nav, 1);
        bar.Children.Add(nav);
        Grid.SetColumn(actions, 2);
        bar.Children.Add(actions);
        titlebar.Child = bar;
        // In narrow windows the page buttons need the room the name takes; the logo still identifies the app.
        bar.SizeChanged += (_, e) => brandName.IsVisible = e.NewSize.Width >= 1060;
        // Dragging the bar's background moves the window; double-clicking it maximizes or restores.
        bar.PointerPressed += (_, e) =>
        {
            if (e.Source is not (Grid or StackPanel or Border) || !e.GetCurrentPoint(bar).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2) ToggleMaximizeRequested?.Invoke();
            else DragRequested?.Invoke(e);
        };

        // Connection recovery bar
        recovery.Bind(Border.BackgroundProperty, "recovery-surface");
        var recoveryRow = new DockPanel();
        DockPanel.SetDock(reconnect, Dock.Right);
        recoveryRow.Children.Add(reconnect);
        recoveryRow.Children.Add(new TextBlock
        {
            Text = "Lighting paused after a connection interruption. Reconnect to recover your current setup.", TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(0, 0, 12, 0),
        }.Bind(TextBlock.ForegroundProperty, "recovery-text"));
        recovery.Child = recoveryRow;

        // Pending preview bar, shown on other pages while a lighting preview is unapplied.
        pending.Bind(Border.BackgroundProperty, "pending-surface").Bind(Border.BorderBrushProperty, "line");
        pendingText.Bind(TextBlock.ForegroundProperty, "accent");
        var review = UiKit.Button("Review preview");
        review.Click += async (_, _) => await Run(() => Navigate("Lighting", keepDraft: true));
        var pendingRow = new DockPanel();
        DockPanel.SetDock(review, Dock.Right);
        pendingRow.Children.Add(review);
        pendingRow.Children.Add(pendingText);
        pending.Child = pendingRow;

        // View over the surface color and the theme's canvas tint, drawn once into a cached image.
        canvas.Children.Add(new CanvasBackdrop());
        canvas.Children.Add(view);
        view.Child = Loading("Opening lighting workspace…");

        // Status bar
        var statusbar = new Border { MinHeight = 29, Padding = new Thickness(18, 4), BorderThickness = new Thickness(0, 1, 0, 0) };
        statusbar.Bind(Border.BackgroundProperty, "chrome").Bind(Border.BorderBrushProperty, "line");
        controller.IsVisible = false;
        var connections = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18, VerticalAlignment = VerticalAlignment.Center, Children = { connection, controller } };
        var statusRow = new DockPanel();
        DockPanel.SetDock(connections, Dock.Right);
        statusRow.Children.Add(connections);
        statusRow.Children.Add(status);
        status.Bind(TextBlock.ForegroundProperty, "muted");
        statusbar.Child = statusRow;

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto") };
        layout.Children.Add(titlebar);
        Grid.SetRow(recovery, 1);
        layout.Children.Add(recovery);
        Grid.SetRow(pending, 2);
        layout.Children.Add(pending);
        Grid.SetRow(canvas, 3);
        layout.Children.Add(canvas);
        Grid.SetRow(statusbar, 4);
        layout.Children.Add(statusbar);
        var root = new Panel { Children = { layout, Overlay } };
        root.Bind(Panel.BackgroundProperty, "surface");
        root[!Avalonia.Controls.Documents.TextElement.ForegroundProperty] = Look.Res("body-text");
        root[!Avalonia.Controls.Documents.TextElement.FontSizeProperty] = new Avalonia.Data.Binding { Source = 13.0 };
        root.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Overlay.HandleEscape()) e.Handled = true;
        };
        return root;
    }

    Button NavButton(string name, string icon, string classes)
    {
        var glyph = new Icon(icon, 17) { VerticalAlignment = VerticalAlignment.Center };
        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { glyph, new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center } } },
            Classes = { classes },
        };
        Avalonia.Automation.AutomationProperties.SetName(button, name);
        button.Click += async (_, _) => await Run(() => Navigate(name));
        return button;
    }

    /// <summary>A status-bar connection label: the state line on one 12px line.</summary>
    static StateLine StatusLine(string text) =>
        new(text, "is-disconnected") { FontSize = 12, LineHeight = double.NaN, TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };

    static Control Loading(string text)
    {
        var spinner = new Spinner();
        return new StackPanel
        {
            Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { spinner, new TextBlock { Text = text, HorizontalAlignment = HorizontalAlignment.Center }.Bind(TextBlock.ForegroundProperty, "muted") },
        };
    }

    // ---- Status and notifications

    public void Status(string message, bool error = false)
    {
        status.Text = message;
        status.Bind(TextBlock.ForegroundProperty, error ? "danger" : "muted");
    }

    public void Notify(string message, bool error = false)
    {
        Status(message, error);
        Overlay.Toast(message, error);
    }

    /// <summary>Runs an action; a failure is shown and the state read again (port of run).</summary>
    public async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception exception)
        {
            Notify(exception.Message is { Length: > 0 } message ? message : "Could not confirm the change. Try again.", true);
            try { await Workspace.Refresh(); } catch (Exception) { }
        }
    }

    public void UpdateConnection()
    {
        var (label, tone) = serviceStopped
            ? ("EE service stopped", "error")
            : SwitchStatus(Workspace.Session, Workspace.Disconnected, Workspace.Busy > 0, Workspace.State.Truthy("reconnect_required"));
        connection.Set(label, tone);
        recovery.IsVisible = Workspace.Ready && Workspace.State.Bool("reconnect_required") == true;
        controller.IsVisible = Workspace.Ready && Workspace.Session.Bool("simulated") != true;
        var (controllerLabel, controllerTone) = ControllerStatus(Workspace.State["controller"], Workspace.Disconnected);
        controller.Set(controllerLabel, controllerTone);
        foreach (var (name, button) in pageButtons) button.IsEnabled = Workspace.Ready || name == "Help";
        if (Workspace.Busy == 0) UpdateDraftBar();
    }

    /// <summary>Port of connectionLabel and switchConnectionStatus (workspace-session.mjs).</summary>
    public static (string Label, string Tone) SwitchStatus(JsonNode? session, bool disconnected, bool busy, bool? reconnectRequired = null)
    {
        string label;
        if (disconnected) label = "Reconnecting…";
        else if (session.Truthy("connecting")) label = "Connecting…";
        else if (!session.Truthy("logged_in")) label = "Not connected";
        else if (reconnectRequired ?? session.Truthy("reconnect_required")) label = "Switch connection needs attention";
        else if (busy) label = "Saving…";
        else if (session.Truthy("simulated")) label = "Demo session";
        else label = session.Truthy("connected") ? "Switch connected" : "Switch reconnecting…";
        var tone = "is-disconnected";
        if (!session.Truthy("simulated"))
        {
            if ((reconnectRequired ?? session.Truthy("reconnect_required")) && !disconnected) tone = "error";
            else if (disconnected || session.Truthy("connecting") || (session.Truthy("logged_in") && session.Bool("connected") != true)) tone = "waiting";
            else if (session.Bool("logged_in") == true && session.Bool("connected") == true) tone = "online";
        }
        return (label, tone);
    }

    public static (string Label, string Tone) ControllerStatus(JsonNode? controller, bool unavailable)
    {
        if (unavailable) return ("Controller status unavailable", "is-disconnected");
        if (controller.Bool("connected") == true)
            return controller.Truthy("stale") ? ("Controller connected · Waiting for fresh data", "waiting") : ("Controller connected", "online");
        // EE retries a controller that does not answer on its own, so that is not an error for the user to fix.
        if (controller.Str("reconnect_state") == "retrying") return ("Controller reconnecting…", "waiting");
        return controller.Truthy("error") ? ("Controller connection needs attention", "error") : ("Controller not connected", "is-disconnected");
    }

    public void UpdateDraftBar()
    {
        pending.IsVisible = Studio.HasEdits && PageName is not ("Lighting" or "Effects");
        if (pending.IsVisible) pendingText.Text = Studio.DraftSummary();
    }

    // ---- Pages

    void OnRefreshed(bool rebuild)
    {
        UpdatePage();
        if (PageName == "Lighting") Studio.Refreshed(rebuild);
    }

    void OnStateMessage()
    {
        UpdatePage();
        if (PageName == "Lighting") Studio.Refreshed(false);
    }

    void OnFrame()
    {
        if (Hidden) return;
        current?.Frame();
        if (PageName == "Settings") current?.Update();
    }

    void UpdatePage()
    {
        if (Hidden) return;
        current?.Update();
    }

    /// <summary>True while the window is hidden or minimized; pages skip work nobody sees.</summary>
    public bool Hidden { get; private set; }

    public async Task SetHidden(bool hidden)
    {
        if (Hidden == hidden) return;
        Hidden = hidden;
        current?.VisibilityChanged(!hidden);
        if (hidden) Studio.SaveUiState(PageName);
        await Workspace.SetPaused(hidden);
        if (hidden || Hidden) return;
        UpdateConnection();
        current?.Update();
    }

    void ShowPage(Page page)
    {
        current?.Leave();
        current = page;
        if (Hidden) page.VisibilityChanged(false);
        Control content = page;
        if (!page.FullBleed)
            content = new ScrollViewer
            {
                Content = new Border { Padding = new Thickness(24), Child = page }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        view.Child = content;
        Overlay.Enter(content, 4, 200);
    }

    void ShowView(Control content)
    {
        current?.Leave();
        current = null;
        view.Child = content;
    }

    /// <summary>The bundled service did not start: say so and offer to start it again.</summary>
    public void ShowServiceFailure(string message, Func<Task> retry)
    {
        var button = UiKit.Button("Try again", "primary");
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await retry(); }
            finally { button.IsEnabled = true; }
        };
        ShowView(UiKit.EmptyState("Couldn’t open the workspace", message, button));
        Status("The EE service is not running", true);
    }

    /// <summary>
    /// The bundled service stopped after it started: stop reconnecting, keep an unapplied preview for the next start,
    /// and offer to start the service again. The status bar says the service stopped for as long as this window shows;
    /// starting the service again opens a new window.
    /// </summary>
    public void ShowServiceStopped(string message, Func<Task> retry)
    {
        serviceStopped = true;
        Workspace.Closing = true;
        Workspace.StopFrames();
        Studio.SaveUiState(PageName);
        ResetWorkspace();
        UpdateConnection();
        ShowServiceFailure(message, retry);
    }

    /// <summary>Opens a page (port of navigate). keepDraft skips the unapplied-preview question.</summary>
    public Task Navigate(string target, bool keepDraft = false) => NavigateCore(target, keepDraft, null);

    internal Task MoveHistory(int step)
    {
        var index = historyIndex + step;
        return index >= 0 && index < history.Count ? NavigateCore(history[index], false, index) : Task.CompletedTask;
    }

    async Task NavigateCore(string target, bool keepDraft, int? destination)
    {
        if (!Workspace.Ready)
        {
            if (target == "Help") ShowLoginHelp();
            return;
        }
        var version = ++navigationVersion;
        if (target == PageName && current is not null) return;
        if (current is not null)
        {
            var allowed = await current.CanLeave();
            if (version != navigationVersion || !allowed) return;
        }
        if (!keepDraft && Studio.HasPendingPlan)
        {
            var allowed = await Studio.ConfirmDraft();
            if (version != navigationVersion || !allowed) return;
        }
        await Workspace.Idle();
        if (version != navigationVersion) return;
        Overlay.CloseAll();
        PageName = target;
        Studio.SaveUiState(PageName);
        foreach (var (name, button) in pageButtons) button.Classes.Set("current", name == PageName);
        ShowPage(PageRegistry.Create(target, this) ?? new MissingPage(this, target));
        if (destination is { } index) historyIndex = index;
        else
        {
            history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
            history.Add(target);
            historyIndex = history.Count - 1;
        }
        UpdateDraftBar();
    }

    // ---- Session flow

    /// <summary>Port of start: reads the effect catalog, session and workspace options, then opens the stream.</summary>
    public async Task Start()
    {
        try
        {
            var effects = Workspace.Request("/api/effects");
            var session = Workspace.Request("/api/session");
            var options = Workspace.Request("/api/workspace");
            var about = ReadAbout();
            await Task.WhenAll(effects, session, options);
            Workspace.Effects = (await effects) as JsonArray ?? [];
            // Lighting, its dialogs and previews read the shared renderer, so a newer service's effects are known
            // before any page opens, as in the browser workspace.
            Core.Model.EffectRenderer.SetEffectCatalog(Workspace.Effects);
            Workspace.Session = (await session) as JsonObject ?? [];
            Workspace.Options = (await options) as JsonObject ?? new JsonObject { ["live"] = false };
            Workspace.About = await about;
            EffectsLoaded?.Invoke();
            Workspace.ConnectFrames();
            startedOnce = true;
            if (Workspace.Session.Truthy("logged_in") || Workspace.Session.Truthy("pending_scene_choice")) await AcceptSession(Workspace.Session);
            else ShowConnection();
        }
        catch (ApiException error) when (error.Code == "lan_pairing_required" && Workspace.Lan)
        {
            // A key saved for this address that the computer running EE no longer accepts gets the revoked text.
            await Pair(Workspace.Api.DeviceKey is not null);
        }
        catch (Exception)
        {
            var retry = UiKit.Button("Try again", "primary");
            retry.Click += async (_, _) => await Start();
            ShowConnectionProblem(Workspace.Lan ? Connections.LanUnreachable(OperatingSystem.IsMacOS()) : "Check the workspace connection and try again.", retry);
        }

        // A host that predates /api/about still opens; About then shows no service version.
        async Task<JsonObject> ReadAbout()
        {
            try { return await Workspace.Request("/api/about") as JsonObject ?? []; }
            catch (ApiException) { return []; }
        }
    }

    /// <summary>
    /// Asks for a pairing code for this LAN workspace and opens it once paired. After Cancel the window says why it
    /// cannot open the workspace and offers pairing again.
    /// </summary>
    async Task Pair(bool revoked)
    {
        pairingShown = true;
        bool paired;
        try { paired = await Pages.Connect.LanPairingDialog.Show(this, revoked); }
        finally { pairingShown = false; }
        if (paired)
        {
            await Start();
            return;
        }
        var again = UiKit.Button("Pair", "primary");
        again.Click += async (_, _) => await Pair(revoked);
        ShowConnectionProblem("Pair this computer with EE to open this workspace.", again);
    }

    /// <summary>
    /// The stream of a LAN workspace closed. A device whose pairing was removed stops reconnecting and pairs again; any
    /// other answer, or none, leaves the stream to reconnect on its own.
    /// </summary>
    async Task CheckPairing()
    {
        if (pairingShown) return;
        JsonNode? info;
        try { info = await Workspace.Api.GetAsync("/api/workspace"); }
        catch (ApiException) { return; }
        if (info.Bool("paired") != false || pairingShown || Workspace.Closing) return;
        Workspace.StopFrames();
        Studio.SaveUiState(PageName);
        ResetWorkspace();
        await Pair(revoked: true);
    }

    /// <summary>
    /// The workspace could not be opened: the reason, the retry if there is one, and Change workspace address for a
    /// window that chooses its host.
    /// </summary>
    public void ShowConnectionProblem(string message, Button? retry = null)
    {
        Control? actions = retry;
        if (OpenWorkspaceAddress is not null)
        {
            var change = UiKit.Button("Change workspace address", retry is null ? "primary" : null, ChooseWorkspaceAddress);
            actions = retry is null ? change : UiKit.Row(8, retry, change);
        }
        ShowView(UiKit.EmptyState("Couldn’t open the workspace", message, actions));
        Status("Connection unavailable", true);
    }

    /// <summary>The effect catalog was read (setEffectCatalog).</summary>
    public event Action? EffectsLoaded;

    public bool Started => startedOnce;

    /// <summary>Port of acceptSession: opens the workspace for a signed-in switch or shows the connection view.</summary>
    public async Task AcceptSession(JsonObject? next)
    {
        if (Workspace.Closing || next is null) return;
        if (PageRegistry.SceneChoice is { } choose) next = await choose(this, next);
        if (next is null || Workspace.Closing) return;
        var changed = Workspace.Session.Str("workspace_id") != next.Str("workspace_id") || Workspace.Session.Bool("simulated") != next.Bool("simulated");
        if (changed && (Workspace.Ready || opening is not null))
        {
            Studio.SaveUiState(PageName);
            ResetWorkspace();
        }
        Workspace.Session = next;
        UpdateConnection();
        if (!Workspace.Session.Truthy("logged_in"))
        {
            if (!Workspace.Ready && current is not null && PageName == "Connect") current.Update();
            else ShowConnection();
            if (PageRegistry.HandoffStatus?.Invoke(Workspace.Session) is { } handoff) Status(handoff.Message, handoff.Error);
            return;
        }
        if (Workspace.Ready) return;
        var workspace = Workspace.WorkspaceId;
        if (opening is not null && openingWorkspace?.Str("workspace_id") == workspace)
        {
            await opening;
            return;
        }
        var initializing = Open(workspace);
        opening = initializing;
        openingWorkspace = next;
        try { await initializing; }
        catch (Exception)
        {
            if (workspace != Workspace.WorkspaceId) return;
            Workspace.Ready = false;
            UpdateConnection();
            var retry = UiKit.Button("Reopen workspace", "primary");
            retry.Click += async (_, _) => await AcceptSession(Workspace.Session);
            ShowView(UiKit.EmptyState("The switch is connected", "Its workspace could not load. Retry to read the current state.", retry));
            Status("Workspace state unavailable", true);
        }
        finally
        {
            if (opening == initializing) { opening = null; openingWorkspace = null; }
        }
    }

    async Task Open(string? workspace)
    {
        if (PageName == "Connect") { current?.Leave(); current = null; }
        await Workspace.Refresh(false);
        if (workspace != Workspace.WorkspaceId || !Workspace.Session.Truthy("logged_in")) return;
        var saved = await Studio.Restore();
        var target = Studio.HasEdits ? "Lighting" : saved is not null && PageNames.Contains(saved) ? saved : "Lighting";
        Workspace.Ready = true;
        UpdateConnection();
        PageName = "";
        await Navigate(target, keepDraft: true);
        Status(Studio.HasEdits ? "Restored unsaved preview" : "Lighting output");
    }

    /// <summary>Port of resetWorkspace: closes dialogs and clears everything tied to the previous switch.</summary>
    public void ResetWorkspace()
    {
        navigationVersion++;
        history.Clear();
        historyIndex = -1;
        Overlay.CloseAll();
        current?.Leave();
        current = null;
        Workspace.State = [];
        Workspace.Bandwidth.Reset();
        Studio.Reset();
        Workspace.Ready = false;
        WorkspaceReset?.Invoke();
    }

    /// <summary>Raised when the workspace is reset for a different switch, for pages that cache per-switch data.</summary>
    public event Action? WorkspaceReset;

    /// <summary>Port of showConnection: the switch connection view for a signed-out session.</summary>
    public void ShowConnection()
    {
        if (Workspace.Ready) ResetWorkspace();
        PageName = "Connect";
        foreach (var (_, button) in pageButtons) button.Classes.Set("current", false);
        ShowPage(PageRegistry.Create("Connect", this) ?? new MissingPage(this, "Connect your switch"));
        var handoff = PageRegistry.HandoffStatus?.Invoke(Workspace.Session);
        UpdateConnection();
        Status(handoff?.Message ?? (Workspace.Session.Truthy("connecting") ? "Connecting with saved login…" : "Connect your switch"), handoff?.Error == true);
    }

    /// <summary>Help over the connection view, with a button back to it (showLoginHelp).</summary>
    public void ShowLoginHelp()
    {
        if (PageRegistry.Create("Help", this) is not { } help) return;
        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(help);
        var dialog = Overlay.Show(content, width: 760);
        content.Children.Add(UiKit.Button("Back to connection", null, () => dialog.Close()));
    }
}

/// <summary>The 16px loading spinner: a ring in the spinner track color with an accent head, turning every 700 ms.</summary>
public sealed class Spinner : Control
{
    public Spinner()
    {
        Width = Height = 16;
        RenderTransform = new RotateTransform();
        RenderTransformOrigin = RelativePoint.Center;
        // Avalonia supports a looping animation only from a style, which runs while the control is shown.
        // Animation.RunAsync still applies one but returns a failed task, which ended up in desktop.log.
        Styles.Add(new Avalonia.Styling.Style(selector => Avalonia.Styling.Selectors.OfType<Spinner>(selector))
        {
            Animations =
            {
                new Avalonia.Animation.Animation
                {
                    Duration = TimeSpan.FromMilliseconds(700), IterationCount = Avalonia.Animation.IterationCount.Infinite,
                    Children =
                    {
                        new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(0), Setters = { new Avalonia.Styling.Setter(RotateTransform.AngleProperty, 0.0) } },
                        new Avalonia.Animation.KeyFrame { Cue = new Avalonia.Animation.Cue(1), Setters = { new Avalonia.Styling.Setter(RotateTransform.AngleProperty, 360.0) } },
                    },
                },
            },
        });
    }

    public override void Render(DrawingContext context)
    {
        var track = Application.Current?.FindResource("spinner-track") as IBrush ?? Brushes.Gray;
        var head = Application.Current?.FindResource("accent") as IBrush ?? Brushes.White;
        var rect = new Rect(1, 1, 14, 14);
        context.DrawEllipse(null, new Pen(track, 2), rect.Center, 7, 7);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(8, 1), false);
            g.ArcTo(new Point(15, 8), new Size(7, 7), 0, false, SweepDirection.Clockwise);
        }
        context.DrawGeometry(null, new Pen(head, 2, lineCap: PenLineCap.Flat), geometry);
    }
}
