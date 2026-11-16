using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Ui;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Settings;

static class Registration
{
    [ModuleInitializer]
    internal static void Register() => PageRegistry.Register("Settings", shell => new SettingsPage(shell));
}

/// <summary>
/// Port of renderSettings (preview/secondary.mjs): connection status and handoff, switch health, the UniFi Network
/// controller, mobile and remote access, the last session, appearance and about, with a section list on the left
/// that scrolls the pane on the right.
/// </summary>
public sealed class SettingsPage : Page
{
    const string ReleasesUrl = "https://github.com/Views2k/etherlighting-evolved/releases";
    readonly Func<string, string, JsonNode?, Task<JsonNode?>> request;
    readonly Core.Model.OperationGate gate;
    readonly Notice notice = new();
    readonly Border savingLine = new() { Height = 2, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false, IsHitTestVisible = false };
    readonly StackPanel content = new() { Spacing = 18 };
    readonly Control contentPanel;
    readonly Border health;
    readonly TextBlock healthStatus = UiKit.HelpText();
    readonly DetailsList healthDetails = new();
    readonly Panel controllerHost = new();
    readonly Connect.PrivateAccessPanel privateAccess;
    readonly LastSession lastSession;
    readonly ThemeSettings themeSettings;
    readonly Control appearance;
    readonly Control about;
    readonly ScrollViewer pane;
    readonly StackPanel paneBody = new();
    readonly List<(Control Section, Button Button)> sections = [];
    readonly Grid root;
    ControllerConnection? controller;
    StateLine? switchState;
    string? healthKey;
    JsonNode? lastStats;
    DateTime statsAt = DateTime.MinValue, healthPaintedAt = DateTime.MinValue;
    DispatcherTimer? scrollAnimation;
    bool busy, closed;

    public SettingsPage(AppShell shell) : base(shell)
    {
        request = Workspace.WorkspaceRequest();
        Styles.Add(SettingsKit.PageStyles());
        gate = Core.Model.SecondaryModel.CreateOperationGate(SetBusy, error => Status(error.Message is { Length: > 0 } text ? text : "The change could not be completed. Try again.", true));

        // Header (frame): title, description and Refresh connection, with the saving line along its bottom edge.
        var refresh = SettingsKit.ActionButton("Refresh connection", Reload);
        refresh.HorizontalAlignment = HorizontalAlignment.Right;
        var header = SettingsKit.ViewHeader("Settings", "Connections, appearance, and application details.", refresh);
        header.Margin = default;
        savingLine.Bind(Border.BackgroundProperty, "accent");
        var headerFrame = new Panel { Margin = new Thickness(0, 0, 0, 20), Children = { header, savingLine } };

        // Connection (div.settings-content.panel)
        contentPanel = SettingsKit.Panel(content, first: true, maxWidth: 720);

        // Switch health
        var (healthFrame, healthBody) = SettingsKit.AutomationSection();
        health = healthFrame;
        healthBody.Children.Add(SettingsKit.SectionHeading("Switch health"));
        healthBody.Children.Add(UiKit.HelpText("Lighting command activity, CPU, memory, temperature, and storage readings."));
        healthBody.Children.Add(healthStatus);
        healthBody.Children.Add(healthDetails);

        // About
        var aboutBody = new StackPanel { Spacing = 12 };
        aboutBody.Children.Add(SettingsKit.H2("Etherlighting Evolved", "accent", 15));
        aboutBody.Children.Add(SettingsKit.P("Open-source UniFi Etherlighting controller.", "muted"));
        var versions = new DetailsList();
        versions.Set([("App version", App.Version), ("Service version", Workspace.About.Str("version") ?? "Unknown")]);
        aboutBody.Children.Add(versions);
        aboutBody.Children.Add(SettingsKit.P(Workspace.Disclaimer, "muted"));
        aboutBody.Children.Add(SettingsKit.ActionButton("Open help", () => Shell.Navigate("Help"), "quiet"));
        aboutBody.Children.Add(SettingsKit.ActionButton("Releases", () => Connect.ConnectionUi.OpenLink(this, ReleasesUrl), "quiet"));
        if (shell.OpenWorkspaceAddress is not null)
            aboutBody.Children.Add(SettingsKit.ActionButton("Change workspace address", shell.ChooseWorkspaceAddress, "quiet"));
        about = SettingsKit.Panel(aboutBody, maxWidth: 720);
        about.Margin = new Thickness(0, 20, 0, 0);

        // Remote, Last session and Appearance
        privateAccess = new Connect.PrivateAccessPanel(request);
        lastSession = new LastSession(request);
        themeSettings = new ThemeSettings();
        appearance = SettingsKit.Panel(themeSettings, maxWidth: 960);

        // Section list and pane
        pane = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new Border { Padding = new Thickness(0, 0, 20, 0), Child = paneBody },
        };
        var navigation = new StackPanel { Spacing = 4 };
        Avalonia.Automation.AutomationProperties.SetName(navigation, "Settings sections");
        foreach (var (section, label) in new (Control, string)[]
                 {
                     (contentPanel, "Connection"), (health, "Health"), (controllerHost, "UniFi"), (privateAccess, "Remote"), (lastSession, "Last session"), (appearance, "Appearance"),
                     (about, "About"),
                 })
        {
            var button = UiKit.Button(label, "quiet settings-nav");
            var target = section;
            button.Click += (_, _) => ScrollTo(target);
            sections.Add((section, button));
            navigation.Children.Add(button);
            paneBody.Children.Add(section);
        }
        var navigationFrame = new Border { BorderThickness = new Thickness(0, 0, 1, 0), Padding = new Thickness(0, 0, 12, 0), Child = navigation };
        navigationFrame.Bind(Border.BorderBrushProperty, "line");
        var workspace = new Grid { ColumnDefinitions = new ColumnDefinitions("164,*"), ColumnSpacing = 24 };
        workspace.Children.Add(navigationFrame);
        Grid.SetColumn(pane, 1);
        workspace.Children.Add(pane);
        pane.ScrollChanged += (_, _) => UpdateSection();
        pane.SizeChanged += (_, _) => UpdateSection();

        root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(24) };
        root.Children.Add(headerFrame);
        Grid.SetRow(workspace, 1);
        root.Children.Add(workspace);
        Grid.SetRow(notice, 2);
        root.Children.Add(notice);
        Content = root;
        Inert.Attach(root, () => busy);

        content.Children.Add(Loading("Loading connection details…"));
        _ = Reload();
    }

    public override bool FullBleed => true;

    void Status(string message = "", bool error = false) => notice.Set(message, error);

    /// <summary>The operation gate's busy state: the page ignores input and the header shows the saving line.</summary>
    void SetBusy(bool value)
    {
        busy = value;
        savingLine.IsVisible = value;
        Cursor = value ? new Cursor(StandardCursorType.AppStarting) : null;
    }

    static Control Loading(string text) => new Border
    {
        MinHeight = 180, Padding = new Thickness(24),
        Child = new TextBlock { Text = text, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
            .Bind(TextBlock.ForegroundProperty, "muted"),
    };

    /// <summary>div.retry-panel: a muted message and a Retry button, centered.</summary>
    void RetryPanel(string message)
    {
        content.Children.Clear();
        var retry = SettingsKit.ActionButton("Retry", Reload);
        retry.HorizontalAlignment = HorizontalAlignment.Center;
        var panel = new StackPanel { Spacing = 14, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = message, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 19.5 }.Bind(TextBlock.ForegroundProperty, "muted"));
        panel.Children.Add(retry);
        content.Children.Add(new Border { MinHeight = 180, Padding = new Thickness(24), Child = panel });
    }

    void UpdateHealth(JsonNode? state, bool unavailable = false)
    {
        var now = DateTime.UtcNow;
        var stats = state.At("stats");
        if (!ReferenceEquals(stats, lastStats))
        {
            lastStats = stats;
            statsAt = now;
        }
        if (!unavailable && (now - healthPaintedAt).TotalMilliseconds < 250) return;
        healthPaintedAt = now;
        var elapsed = (now - statsAt).TotalMilliseconds;
        // Sample ages count on from when the stats arrived, as the browser workspace does between frames.
        var agedStats = lastStats is JsonObject current ? (JsonObject)current.DeepClone() : [];
        foreach (var key in new[] { "cpu_sample_age_ms", "health_sample_age_ms" })
            if (lastStats.Num(key) is { } age) agedStats[key] = age + elapsed;
        // Only the fields the readings use are copied.
        var aged = new JsonObject { ["stats"] = agedStats };
        foreach (var name in new[] { "poe_summary", "connected", "reconnect_required" }) aged[name] = state.At(name)?.DeepClone();
        var (status, rows) = SwitchHealth.Read(aged, unavailable);
        var key2 = new JsonArray(status, new JsonArray(rows.Select(row => (JsonNode?)new JsonArray(row.Label, row.Value)).ToArray())).ToJsonString();
        if (key2 == healthKey) return;
        healthKey = key2;
        healthStatus.Text = status;
        healthDetails.Set(rows, label => label == "Load average" ? "Average running and waiting tasks; this is not a CPU percentage." : null);
    }

    Task Reload() => gate.Run(async () =>
    {
        try
        {
            var sessionTask = request("/api/session", "GET", null);
            var stateTask = request("/api/state", "GET", null);
            await Task.WhenAll(sessionTask, stateTask);
            var session = await sessionTask;
            var state = await stateTask;
            if (!gate.Active) return;
            var simulated = session.Truthy("simulated");
            // A window that uses private access cannot forget the saved switch login or set up UniFi Network.
            var remote = Workspace.Remote;
            health.IsVisible = !simulated;
            sections[1].Button.IsVisible = !simulated;
            sections[2].Button.IsVisible = !simulated && !remote;
            controllerHost.IsVisible = !remote;
            UpdateHealth(state);
            content.Children.Clear();
            switchState = new StateLine();
            switchState.Set(AppShell.SwitchStatus(session, false, false));
            content.Children.Add(SettingsKit.H2("Connection", "accent"));
            content.Children.Add(switchState);
            if (simulated) content.Children.Add(SettingsKit.P("Use this preview to review the lighting workspace, effects, rules, and profiles.", "muted"));
            else
            {
                if (session.At("offline_lighting_error") is { } offline && offline.Truthy()) content.Children.Add(UiKit.HelpText(offline.Str() ?? offline.ToJsonString()));
                Button? handoff = null;
                handoff = SettingsKit.ActionButton("Handoff to UniFi", () => gate.Run(async () =>
                {
                    var unsaved = controller?.HasChanges == true || privateAccess.HasChanges;
                    var message = "Stop EE lighting and its switch helpers, close the SSH connection, and return lighting control to UniFi. Saved logins, profiles, and your lighting setup are kept. EE will wait until you connect again." +
                                  (unsaved ? " Unapplied connection changes will be discarded." : "") +
                                  (remote && !Connections.RemoteCanRestore(session) ? " The computer running EE has no saved switch login that can be opened, so you will need that computer to connect again." : "");
                    if (!await Confirm("Handoff to UniFi?", message, "Handoff to UniFi") || !gate.Active) return;
                    handoff!.SetLabel("Handing off…");
                    Status("Handing lighting back to UniFi…");
                    try
                    {
                        if (await Connections.RequestHandoff(request, () => gate.Active) is not { } result) return;
                        Status(result.Error ?? "Lighting returned to UniFi.", result.Error is not null);
                        if (result.Session?.Bool("logged_in") == false)
                        {
                            await Shell.AcceptSession(result.Session);
                            if (result.Error is { } error) Shell.Notify(error, true);
                        }
                    }
                    finally
                    {
                        if (gate.Active) handoff!.SetLabel("Handoff to UniFi");
                    }
                }));
                content.Children.Add(UiKit.HelpText("Return lighting control to UniFi and keep your EE setup for next time."));
                content.Children.Add(handoff);
                if (session.Truthy("has_saved_login") && !remote)
                {
                    Button? forget = null;
                    forget = SettingsKit.ActionButton("Forget saved switch login", () => gate.Run(async () =>
                    {
                        if (!await Confirm("Forget saved switch login?", "Remove the saved SSH login from this PC. The current switch connection stays open.", "Forget login") || !gate.Active) return;
                        await request("/api/session/forget", "POST", null);
                        content.Children.Remove(forget!);
                        Status("Saved switch login removed.");
                    }), "quiet");
                    content.Children.Add(forget);
                }
                if (controller is null && !remote)
                {
                    controller = new ControllerConnection(request, _ => Workspace.Refresh(false));
                    controllerHost.Children.Add(SettingsKit.LeftMax(controller, 720));
                }
            }
            var (copper, sfp) = Core.Model.PortTopology.LightingPorts(state);
            var details = new DetailsList();
            details.Set([
                ("Lighting", state.Str("mode") == "released" ? "Controlled by UniFi" : "Controlled by EE"),
                ("Ethernet ports", copper.Count.ToString()),
                ("SFP lights", sfp.Count.ToString()),
            ]);
            content.Children.Add(details);
            Status();
            Dispatcher.UIThread.Post(UpdateSection, DispatcherPriority.Background);
        }
        catch (Exception)
        {
            RetryPanel("Connection details could not be loaded.");
            throw;
        }
    });

    async Task<bool> Confirm(string title, string message, string accept) => !closed && await Shell.Overlay.Confirm(title, message, accept);

    // ---- Section list

    double SectionTop(Control section) => section.TranslatePoint(default, pane)?.Y ?? 0;

    void ScrollTo(Control section)
    {
        var target = Math.Clamp(pane.Offset.Y + SectionTop(section), 0, Math.Max(0, pane.Extent.Height - pane.Viewport.Height));
        var start = pane.Offset.Y;
        scrollAnimation?.Stop();
        if (Math.Abs(target - start) < 1) return;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        scrollAnimation = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) =>
        {
            var progress = Math.Min(1, clock.Elapsed.TotalMilliseconds / 320);
            var eased = progress < 0.5 ? 4 * progress * progress * progress : 1 - Math.Pow(-2 * progress + 2, 3) / 2;
            pane.Offset = new Vector(pane.Offset.X, start + (target - start) * eased);
            if (progress >= 1) scrollAnimation?.Stop();
        });
        scrollAnimation.Start();
    }

    void UpdateSection()
    {
        var visible = sections.Select((item, index) => (item.Section, item.Button, Index: index))
            .Where(item => item.Section.IsVisible && item.Button.IsVisible).ToList();
        int? selected = null;
        if (visible.Count > 0)
        {
            var atEnd = pane.Extent.Height > pane.Viewport.Height && pane.Offset.Y + pane.Viewport.Height >= pane.Extent.Height - 2;
            if (atEnd) selected = visible[^1].Index;
            else
            {
                selected = visible[0].Index;
                foreach (var item in visible)
                    if (SectionTop(item.Section) <= 24) selected = item.Index;
            }
        }
        for (var index = 0; index < sections.Count; index++) sections[index].Button.Classes.Set("selected", index == selected);
    }

    // ---- Page

    public override void Update()
    {
        if (!gate.Active) return;
        var unavailable = Workspace.Disconnected;
        switchState?.Set(AppShell.SwitchStatus(Workspace.Session, unavailable, false));
        UpdateHealth(Workspace.State, unavailable);
        if (Workspace.State["controller"] is { } value && value.Truthy()) controller?.Update(value, unavailable);
    }

    public override bool HasChanges => controller?.HasChanges == true || privateAccess.HasChanges;

    public override async Task<bool> CanLeave()
    {
        await gate.Settle();
        await privateAccess.CanLeave();
        if (!gate.Active) return false;
        if (HasChanges && !await Confirm("Leave connection settings?", "Your unapplied connection changes will be discarded.", "Leave")) return false;
        return gate.Active && (controller is null || await controller.CanLeave());
    }

    public override void Leave()
    {
        closed = true;
        scrollAnimation?.Stop();
        controller?.Close();
        privateAccess.Close();
        lastSession.Close();
        themeSettings.Close();
        gate.Close();
        SetBusy(false);
    }
}
