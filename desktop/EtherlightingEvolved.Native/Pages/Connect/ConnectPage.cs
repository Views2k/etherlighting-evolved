using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Shell;
using EtherlightingEvolved.Native.Theme;
using static EtherlightingEvolved.Native.Pages.Connections;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// The switch connection view shown while no switch is signed in, a port of renderSwitchConnection
/// (preview/connections.mjs): the SSH login form, the saved login, the handoff retry, the demo, connection help and
/// private access. The shell passes the same options as showConnection in preview/app.mjs.
/// </summary>
public sealed class ConnectPage : Page
{
    [ModuleInitializer]
    internal static void Register()
    {
        PageRegistry.Register("Connect", shell => new ConnectPage(shell));
        PageRegistry.SceneChoice = (shell, session) => SceneChoice.Show(shell.Overlay, (path, method, body) => shell.Workspace.Request(path, method, body), session);
        PageRegistry.HandoffStatus = session => HandoffStatus(session) is { } handoff ? (handoff.Message, handoff.Error) : null;
    }

    readonly Func<string, string, JsonNode?, Task<JsonNode?>> request;
    readonly Func<Task<JsonNode?>>? restoreSaved;
    readonly Func<Task>? onDemo;
    readonly CancellationTokenSource lifecycle = new();
    readonly Notice message = new();
    readonly TextBox address = ConnectionUi.Input(253);
    readonly TextBox username = ConnectionUi.Input(128);
    readonly TextBox password = ConnectionUi.Input(password: true);
    readonly TextBox port = ConnectionUi.Input();
    readonly CheckBox remember;
    readonly Button reveal;
    readonly Button submit = ConnectionUi.Button("Connect to switch", "primary");
    readonly Button restore = ConnectionUi.Button("Connect with saved login");
    readonly Button forget = ConnectionUi.Button("Forget saved login", "quiet");
    readonly Button retryHandoff = ConnectionUi.Button("Retry handoff", "primary");
    readonly Button? firstPanelButton;
    readonly List<Control> formElements;
    readonly PrivateAccessPanel privateAccess;
    bool active = true, completed, dirty, forgetting, retryingHandoff, updating;
    Task? pending;
    JsonObject saved;
    JsonObject? lastSession;

    public ConnectPage(AppShell shell) : base(shell)
    {
        request = (path, method, body) => Workspace.Request(path, method, body);
        restoreSaved = () => Workspace.Request("/api/session/restore?resume=true", "POST");
        if (!Workspace.Options.Truthy("live"))
            onDemo = async () => await Shell.AcceptSession(await Workspace.Request("/api/session/login", "POST", new JsonObject { ["host"] = "demo" }) as JsonObject);
        var session = Workspace.Session;
        saved = session;

        var panel = new StackPanel { Classes = { "connection-panel", "panel" } };
        var logo = ConnectionUi.Logo(52);
        logo.Margin = new Thickness(0, 0, 0, 5);
        var heading = new TextBlock
        {
            Text = "Connect your switch", Classes = { "h1" }, FontSize = 24, LineHeight = 34.8, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 8),
        };
        var intro = ConnectionUi.HelpText("Enter the switch’s SSH login to open its lighting workspace.");
        intro.Margin = default;
        panel.Children.Add(logo);
        panel.Children.Add(heading);
        panel.Children.Add(intro);

        port.Text = "22";
        (var rememberLabel, remember) = ConnectionUi.CheckRow("Save login on this PC", "text", 32);
        var options = new Disclosure("Connection options");
        var portField = ConnectionUi.Field("SSH port", port);
        portField.Margin = new Thickness(0, 14, 0, 0);
        portField.Width = 150;
        portField.HorizontalAlignment = HorizontalAlignment.Left;
        options.Body.Children.Add(portField);
        var actions = new FlowRow(10, submit) { Classes = { "connection-actions" } };
        if (restoreSaved is not null) actions.Children.Add(restore);
        actions.Children.Add(forget);
        actions.Children.Add(retryHandoff);
        retryHandoff.IsVisible = false;
        var (passwordField, revealButton) = ConnectionUi.PasswordField("SSH password", password);
        reveal = revealButton;
        var form = new StackPanel { Spacing = 18, Margin = new Thickness(0, 22, 0, 0), Classes = { "connection-form" } };
        form.Children.Add(ConnectionUi.Field("Switch address", address));
        form.Children.Add(ConnectionUi.Field("SSH username", username));
        form.Children.Add(passwordField);
        form.Children.Add(rememberLabel);
        form.Children.Add(options);
        form.Children.Add(actions);
        panel.Children.Add(message);
        panel.Children.Add(form);
        formElements = [address, username, password, reveal, remember, port, submit];
        if (restoreSaved is not null) formElements.Add(restore);
        formElements.Add(forget);
        formElements.Add(retryHandoff);

        // The demo and help buttons sit side by side below the form, as inline buttons do.
        var inline = new FlowRow(0);
        if (onDemo is not null)
        {
            var demo = ConnectionUi.Button("Continue with demo", "quiet");
            demo.Click += async (_, _) =>
            {
                if (pending is not null || saved.Truthy("connecting") || HandoffBlocked(saved)) return;
                try { await onDemo(); }
                catch (Exception) { }
            };
            inline.Children.Add(demo);
            firstPanelButton = demo;
        }
        var help = ConnectionUi.Button("Connection help", "quiet");
        help.Click += (_, _) => Shell.ShowLoginHelp();
        inline.Children.Add(help);
        firstPanelButton ??= help;
        panel.Children.Add(inline);

        privateAccess = new PrivateAccessPanel(request, lifecycle.Token) { Classes = { "connection-private-access" } };
        panel.Children.Add(privateAccess);
        panel.Children.Add(new TextBlock
        {
            Text = Workspace.Disclaimer, FontSize = 12, LineHeight = 19.2, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 24, 0, 0),
        }.Bind(TextBlock.ForegroundProperty, "muted"));

        // section.connection-panel.panel: 440px wide, 32px from the view's edges, with the panel's top rule.
        Content = new Border
        {
            Width = 440, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 32), Padding = new Thickness(28),
            BorderThickness = new Thickness(0, 1, 0, 0), Child = panel,
        }.Bind(Border.BorderBrushProperty, "line");

        foreach (var input in new[] { address, username, password, port })
        {
            input.TextChanging += (_, _) =>
            {
                if (updating) return;
                dirty = true;
                input.Classes.Remove("invalid");
            };
            input.KeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (submit.IsEffectivelyEnabled) Submit();
            };
        }
        remember.IsCheckedChanged += (_, _) =>
        {
            if (!updating) dirty = true;
        };
        submit.Click += (_, _) => Submit();
        restore.Click += (_, _) => Connect(restoreSaved!);
        retryHandoff.Click += (_, _) => RetryHandoff();
        forget.Click += async (_, _) => await Forget();
        lastSession = session;
        Apply(session);
    }

    public override bool HasChanges => dirty || privateAccess.HasChanges;

    public override async Task<bool> CanLeave()
    {
        if (pending is { } running) await running;
        await privateAccess.CanLeave();
        return true;
    }

    /// <summary>The shell accepted a session while this view shows (the renderer's update).</summary>
    public override void Update()
    {
        if (ReferenceEquals(Workspace.Session, lastSession)) return;
        lastSession = Workspace.Session;
        Apply(Workspace.Session);
    }

    public override void Leave()
    {
        active = false;
        lifecycle.Cancel();
        updating = true;
        password.Text = "";
        updating = false;
        privateAccess.Close();
    }

    void Status(string text = "", bool error = false, string tone = "") => message.Set(text, error, tone);

    /// <summary>Starts work that keeps the form busy until it ends (the browser's pending promise).</summary>
    void StartPending(Func<Task> work)
    {
        var task = work();
        if (!task.IsCompleted) pending = task;
    }

    void SetBusy()
    {
        if (!active) return;
        var blocked = HandoffBlocked(saved);
        var waiting = pending is not null || saved.Truthy("connecting") || (saved.Truthy("pending_scene_choice") && !blocked) || saved.Str("handoff_state") == "pending";
        var recovering = blocked && saved.Str("handoff_state") != "pending";
        foreach (var control in formElements) control.IsEnabled = !(waiting || recovering);
        retryHandoff.IsVisible = HandoffRetryable(saved);
        retryHandoff.IsEnabled = !(waiting || !HandoffRetryable(saved));
        retryHandoff.Content = retryingHandoff ? "Handing off…" : "Retry handoff";
        submit.Content = saved.Str("handoff_state") == "pending" || retryingHandoff ? "Handing off…" : waiting && !forgetting ? "Connecting…" : "Connect to switch";
        forget.Content = forgetting ? "Removing saved login…" : "Forget saved login";
        restore.IsVisible = restoreSaved is not null && saved.Truthy("has_saved_login");
        forget.IsVisible = saved.Truthy("has_saved_login");
        if (firstPanelButton is not null) firstPanelButton.IsEnabled = !waiting;
    }

    async Task Enter(JsonObject? value)
    {
        if (!active || completed) return;
        var resolved = await SceneChoice.Show(Shell.Overlay, request, value, lifecycle.Token);
        if (!active || completed || resolved is null) return;
        saved = resolved;
        if (HandoffBlocked(resolved))
        {
            var handoff = HandoffStatus(resolved);
            Status(handoff?.Message ?? "", handoff?.Error == true, handoff?.Tone ?? "");
            SetBusy();
            return;
        }
        if (!resolved.Truthy("logged_in"))
        {
            if (value.Truthy("pending_scene_choice")) Status("Lighting setup canceled. You can edit this login and connect again.");
            SetBusy();
            submit.Focus();
            return;
        }
        completed = true;
        updating = true;
        password.Text = "";
        updating = false;
        dirty = false;
        try { await Shell.AcceptSession(resolved); }
        catch (Exception)
        {
            if (active)
            {
                completed = false;
                Status("The switch connected, but the workspace could not load. Try opening it again.", true);
            }
        }
    }

    void Apply(JsonObject? value)
    {
        if (!active) return;
        saved = value ?? [];
        if (!dirty && pending is null)
        {
            updating = true;
            try
            {
                if (string.IsNullOrEmpty(address.Text)) address.Text = saved.Str("remembered.host") ?? "";
                if (string.IsNullOrEmpty(username.Text)) username.Text = saved.Str("remembered.username") ?? "";
                if (saved.At("remembered.port") is { } rememberedPort && rememberedPort.Truthy()) port.Text = rememberedPort.Str() ?? rememberedPort.ToJsonString();
                remember.IsChecked = saved.Bool("has_saved_login") == true;
            }
            finally { updating = false; }
        }
        var handoff = HandoffStatus(saved);
        if (HandoffBlocked(saved) && handoff is not null) Status(handoff.Message, handoff.Error, handoff.Tone);
        else if (saved.Truthy("pending_scene_choice")) Status("Choose lighting to finish connecting.");
        else if (saved.Truthy("connecting")) Status("Connecting with saved login…");
        else if (saved.Truthy("saved_login_error")) Status(Messages["saved_login_unavailable"], true);
        else if (saved.Truthy("error")) Status(ConnectionError(saved["error"]), true);
        else if (handoff is not null) Status(handoff.Message, handoff.Error, handoff.Tone);
        SetBusy();
        if (SceneChoice.IsOpen || (!HandoffBlocked(saved) && (saved.Truthy("logged_in") || saved.Truthy("pending_scene_choice"))))
        {
            var current = saved;
            Dispatcher.UIThread.Post(() => _ = Enter(current));
        }
    }

    void Connect(Func<Task<JsonNode?>> action)
    {
        if (!active || pending is not null || saved.Truthy("connecting") || saved.Truthy("pending_scene_choice") || HandoffBlocked(saved)) return;
        Status("Connecting…");
        StartPending(async () =>
        {
            try
            {
                var result = await action() as JsonObject;
                if (!active) return;
                saved = result ?? [];
                if (!result.Truthy("logged_in") && !result.Truthy("pending_scene_choice")) throw new InvalidOperationException("Connection was not established.");
                await Enter(result);
            }
            catch (Exception error)
            {
                if (active) Status(ConnectionError(error), true);
            }
            finally
            {
                pending = null;
                SetBusy();
                if (active && !completed) submit.Focus();
            }
        });
        SetBusy();
    }

    void Submit()
    {
        if (!active) return;
        var portNumber = JsNumber(port.Text);
        var invalid = new (TextBox Control, bool Failed, string Message)[]
        {
            (address, string.IsNullOrWhiteSpace(address.Text), "Enter the switch address."),
            (username, string.IsNullOrWhiteSpace(username.Text), "Enter the SSH username."),
            (password, string.IsNullOrEmpty(password.Text),
                restoreSaved is not null && saved.Truthy("has_saved_login") ? "Enter the SSH password, or choose Connect with saved login." : "Enter the SSH password."),
            (port, !Core.Model.Js.IsInteger(portNumber) || portNumber < 1 || portNumber > 65535, "Enter an SSH port from 1 to 65535."),
        }.FirstOrDefault(entry => entry.Failed);
        if (invalid.Control is not null)
        {
            invalid.Control.Classes.Add("invalid");
            Status(invalid.Message, true);
            invalid.Control.Focus();
            return;
        }
        var payload = new JsonObject
        {
            ["host"] = address.Text!.Trim(), ["username"] = username.Text!.Trim(), ["password"] = password.Text, ["port"] = (int)portNumber,
            ["remember"] = remember.IsChecked == true,
        };
        Connect(() => request("/api/session/login", "POST", payload));
    }

    void RetryHandoff()
    {
        if (!active || pending is not null || !HandoffRetryable(saved)) return;
        retryingHandoff = true;
        StartPending(async () =>
        {
            try
            {
                if (!await Confirmation.ConfirmAction(Shell.Overlay, this, "Retry handoff to UniFi?", "Stop EE lighting and return control to UniFi. Your saved setup is kept.",
                        "Retry handoff", lifecycle.Token) || !active)
                    return;
                Status("Handing lighting back to UniFi…");
                var result = await RequestHandoff(request, () => active);
                if (result is null) return;
                if (result.Session is { } session)
                {
                    Apply(session);
                    await Shell.AcceptSession(session);
                    if (result.Error is { } notice) Shell.Notify(notice, true);
                }
                if (active && result.Error is { } error) Status(error, true);
            }
            catch (Exception)
            {
                if (active) Status("Handoff status could not be displayed. Choose Retry handoff to check again, or reopen EE.", true);
            }
            finally
            {
                pending = null;
                retryingHandoff = false;
                SetBusy();
                if (active)
                {
                    Control target = HandoffRetryable(saved) ? retryHandoff : HandoffBlocked(saved) ? message
                        : restoreSaved is not null && saved.Truthy("has_saved_login") ? restore : submit;
                    if (target == message) message.Focusable = true;
                    target.Focus();
                }
            }
        });
        SetBusy();
    }

    async Task Forget()
    {
        if (!active || pending is not null || saved.Truthy("connecting") || saved.Truthy("pending_scene_choice") || HandoffBlocked(saved)) return;
        if (!await Confirmation.ConfirmAction(Shell.Overlay, this, "Forget saved login?", "Remove the saved switch login from this PC.", "Forget login", lifecycle.Token)) return;
        if (!active || pending is not null || saved.Truthy("connecting") || saved.Truthy("pending_scene_choice") || HandoffBlocked(saved)) return;
        forgetting = true;
        StartPending(async () =>
        {
            try
            {
                var result = await request("/api/session/forget", "POST", null);
                if (!active) return;
                saved = result as JsonObject ?? [];
                updating = true;
                remember.IsChecked = false;
                updating = false;
                Status("Saved login removed.");
            }
            catch (Exception error)
            {
                if (active) Status(ConnectionError(error, Messages["saved_login_forget_failed"]), true);
            }
            finally
            {
                pending = null;
                forgetting = false;
                SetBusy();
                if (active) submit.Focus();
            }
        });
        SetBusy();
    }
}
