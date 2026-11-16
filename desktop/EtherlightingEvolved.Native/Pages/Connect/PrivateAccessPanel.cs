using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Ui;
using static EtherlightingEvolved.Native.Pages.Connections;
using UiKit = EtherlightingEvolved.Native.Ui.Ui;

namespace EtherlightingEvolved.Native.Pages.Connect;

/// <summary>
/// Mobile and remote access through Tailscale, a port of renderPrivateAccess (preview/connections.mjs). It reads
/// /api/private-access, offers setup, sign-in, the access audience and port, and shows the private address once
/// it is ready. Used on the connection view and in Settings.
/// </summary>
public sealed class PrivateAccessPanel : UserControl
{
    readonly Func<string, string, JsonNode?, Task<JsonNode?>> request;
    readonly CancellationTokenRegistration abortRegistration;
    readonly StackPanel section = new() { Spacing = 12, Classes = { "private-access", "automation-section" } };
    readonly Button refresh = UiKit.Button("Refresh private access", "quiet");
    readonly StateLine state = new("Checking private access…", "waiting");
    readonly StackPanel setup = new() { IsVisible = false };
    readonly TextBlock setupHelp = UiKit.HelpText("Install Tailscale on this computer and your phone or other device. Sign in with the same Tailscale account on both.");
    readonly Button setupLink = UiKit.Button("");
    readonly Button signIn = UiKit.Button("Sign in to Tailscale", "primary");
    readonly CheckRow label = new("Use Tailscale");
    readonly CheckBox toggle;
    readonly SelectBox audience = new();
    readonly StackPanel audienceField;
    readonly TextBlock readOnly = UiKit.HelpText("Change this setting in EE on the host computer.");
    readonly Button apply = UiKit.Button("Apply setting", "primary");
    readonly FlowRow actions;
    readonly Disclosure advanced = new("Connection options");
    readonly TextBox port = UiKit.Input();
    readonly Button resetPort = UiKit.Button("Reset port", "quiet");
    readonly FlowRow address;
    readonly Button open = UiKit.Button("Open private workspace");
    readonly Button copy = UiKit.Button("Copy private address", "quiet");
    readonly TextBlock install = UiKit.HelpText("iPhone or iPad: Share → Add to Home Screen. Android: browser menu → Install app.");
    readonly Notice message = new(connection: true);
    bool active, updating;
    JsonObject? current;
    bool? draft;
    string? portDraft, audienceDraft, pending, setupHref, openHref;
    Task completion = Task.CompletedTask;

    public PrivateAccessPanel(Func<string, string, JsonNode?, Task<JsonNode?>> request, CancellationToken signal = default)
    {
        this.request = request;
        active = !signal.IsCancellationRequested;
        var title = ConnectionUi.Heading("Mobile & remote access");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 18, Classes = { "connection-heading" } };
        title.VerticalAlignment = VerticalAlignment.Top;
        refresh.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(refresh, 1);
        header.Children.Add(title);
        header.Children.Add(refresh);
        var help = UiKit.HelpText("Use Tailscale to open this workspace on your phone or another device.");
        var scope = UiKit.HelpText("Devices on Tailscale can use lighting, effects, automation, profiles, schedules and port names, can control fans in the desktop app, and can reconnect to the saved switch. Switch login, UniFi Network setup and these settings are available only on the computer running EE.");
        setup.Children.Add(setupHelp);
        setup.Children.Add(new FlowRow(10, setupLink, signIn) { Classes = { "connection-actions" } });
        toggle = label.Box;
        audience.SetOptions([("owner", "Only my Tailscale account", false), ("accounts", "Any Tailscale account that can reach this computer", false)]);
        audienceField = UiKit.Field("Who can open EE", audience, "Tailscale decides which accounts can reach this computer. Devices without an account stay blocked.");
        readOnly.IsVisible = false;
        actions = new FlowRow(10, apply) { Classes = { "connection-actions" } };
        var portField = UiKit.Field("Private HTTPS port", port);
        portField.Margin = new Thickness(0, 14, 0, 0);
        portField.MaxWidth = 150;
        portField.HorizontalAlignment = HorizontalAlignment.Left;
        portField.Width = 150;
        resetPort.IsVisible = false;
        resetPort.HorizontalAlignment = HorizontalAlignment.Left;
        advanced.Body.Children.Add(portField);
        advanced.Body.Children.Add(resetPort);
        advanced.Body.Children.Add(UiKit.HelpText("Use a free HTTPS port between 1024 and 65535. Turn off private access before changing it."));
        address = new FlowRow(10, open, copy) { IsVisible = false, Classes = { "connection-actions" } };
        install.IsVisible = false;
        foreach (var child in new Control[] { header, help, scope, state, setup, label, audienceField, advanced, readOnly, actions, address, install, message }) section.Children.Add(child);
        Content = section;
        if (!active) return;

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (updating || !active || pending is not null || !CanConfigure()) return;
            draft = toggle.IsChecked == true == (current.Bool("enabled") == true) ? null : toggle.IsChecked == true;
            Status();
            Render();
        };
        audience.Changed += () =>
        {
            if (!active || pending is not null || !CanConfigure()) return;
            audienceDraft = audience.Value == CurrentAudience() ? null : audience.Value == "accounts" ? "accounts" : "owner";
            Status();
            Render();
        };
        port.TextChanging += (_, _) =>
        {
            if (updating || !active || pending is not null || !CanChangePort()) return;
            var text = port.Text ?? "";
            portDraft = text == CurrentPort().ToString(CultureInfo.InvariantCulture) ? null : text;
            Status(ValidPort() ? "" : "Enter a whole-number port between 1024 and 65535.", !ValidPort());
            Render();
        };
        resetPort.Click += (_, _) =>
        {
            if (!active || pending is not null || !CanConfigure()) return;
            portDraft = null;
            Status();
            Render();
            (port.IsEnabled ? (Control)port : refresh).Focus();
        };
        apply.Click += (_, _) => _ = Start("apply");
        refresh.Click += (_, _) => _ = Start("refresh", true);
        signIn.Click += (_, _) => _ = Start("login");
        setupLink.Click += async (_, _) =>
        {
            if (setupHref is { } href) await ConnectionUi.OpenLink(this, href);
        };
        open.Click += async (_, _) =>
        {
            if (openHref is { } href) await ConnectionUi.OpenLink(this, href);
        };
        copy.Click += async (_, _) =>
        {
            var url = ReadyUrl();
            if (!active || pending is not null || url is null) return;
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) throw new InvalidOperationException("clipboard_unavailable");
                await clipboard.SetTextAsync(url);
                if (active) Status("Private address copied.");
            }
            catch (Exception)
            {
                if (!active) return;
                Status("Open the private workspace and copy its address from your browser.");
                open.Focus();
            }
        };
        abortRegistration = signal.Register(() => active = false);
        Render();
        _ = Start("refresh");
    }

    /// <summary>Ends the panel (the renderer's cleanup): later results are ignored.</summary>
    public void Close()
    {
        active = false;
        abortRegistration.Dispose();
    }

    /// <summary>Shows a private access status read elsewhere (cleanup.update).</summary>
    public void Update(JsonNode? value)
    {
        if (!active) return;
        if (value is not JsonObject obj || obj["enabled"]?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) ||
            obj["available"]?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False) ||
            obj["ready"]?.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new InvalidOperationException("invalid_private_status");
        current = obj;
        if (draft == (current.Bool("enabled") == true)) draft = null;
        if (portDraft is not null && JsNumber(portDraft) == CurrentPort()) portDraft = null;
        if (audienceDraft == CurrentAudience()) audienceDraft = null;
        Render();
    }

    public Task Refresh() => Start("refresh", true);

    public bool HasChanges => draft is not null || portDraft is not null || audienceDraft is not null;

    public async Task<bool> CanLeave()
    {
        await completion;
        return true;
    }

    void Status(string text = "", bool error = false) => message.Set(text, error);

    bool CanConfigure() => current.Bool("can_configure") == true;

    // A local gateway this window did not start, on a live host. The demo gateway reports neither field, so a demo
    // window keeps the general text.
    bool Locked() => current.Bool("admin_required") == true && (current.Bool("available") == true || current?["error"] is JsonObject);

    bool CanRetry() => current.Bool("enabled") == true && current.Bool("available") == true && current.Bool("ready") != true;

    int CurrentPort() => current.Int("https_port") is { } value and >= 1024 and <= 65535 ? value : 8444;

    string? ReadyUrl()
    {
        var url = PrivateAccessUrl(current.Str("url"));
        return current.Bool("available") == true && current.Bool("enabled") == true && current.Bool("ready") == true && url is not null &&
               new Uri(url).Port == CurrentPort() ? url : null;
    }

    bool CanChangePort() => CanConfigure() && current.Bool("can_change_port") == true && current.Bool("enabled") != true && current.Bool("ready") != true;

    bool ValidPort()
    {
        var text = portDraft ?? CurrentPort().ToString(CultureInfo.InvariantCulture);
        var number = JsNumber(text);
        return Regex.IsMatch(text, @"^\d+$") && number >= 1024 && number <= 65535;
    }

    bool HasPortChange() => portDraft is not null && JsNumber(portDraft) != CurrentPort();

    string CurrentAudience() => current.Str("audience") == "accounts" ? "accounts" : "owner";

    bool HasAudienceChange() => audienceDraft is not null && audienceDraft != CurrentAudience();

    void Render()
    {
        if (!active) return;
        updating = true;
        try
        {
            var enabled = current.Bool("enabled") == true;
            var url = ReadyUrl();
            var ready = url is not null;
            var chosen = draft ?? enabled;
            var busy = pending is not null;
            toggle.IsChecked = chosen;
            toggle.IsEnabled = !(busy || !CanConfigure() || (current.Bool("available") != true && !enabled));
            var portChange = HasPortChange();
            var portAllowed = !portChange || CanChangePort();
            var audienceChange = HasAudienceChange();
            audience.Value = audienceDraft ?? CurrentAudience();
            audience.IsEnabled = !(busy || !CanConfigure());
            var audienceHidden = current is null || !CanConfigure();
            apply.IsEnabled = !(busy || !CanConfigure() || !ValidPort() || !portAllowed ||
                                (chosen == enabled && !portChange && !audienceChange && !(chosen && CanRetry())) || chosen && current.Bool("available") != true);
            apply.Content = pending == "apply" ? "Applying…" : chosen == enabled && CanRetry() ? "Retry private access" : "Apply setting";
            refresh.IsEnabled = !busy;
            readOnly.IsVisible = !(current is null || CanConfigure());
            readOnly.Text = Locked() ? AdminRequired : "Change this setting in EE on the host computer.";
            var portText = portDraft ?? CurrentPort().ToString(CultureInfo.InvariantCulture);
            if (port.Text != portText) port.Text = portText;
            port.IsEnabled = !(busy || !CanChangePort());
            var advancedHidden = current is null || !CanConfigure();
            resetPort.IsVisible = portDraft is not null;
            resetPort.IsEnabled = !(busy || !CanConfigure());
            var setupAction = current.Str("setup_action");
            var setupUrl = PrivateAccessSetupUrl(current.Str("action_url"), setupAction);
            setup.IsVisible = !(!CanConfigure() || setupAction is not ("install" or "signin" or "approve"));
            setupLink.IsVisible = setupUrl is not null;
            setupHref = setupUrl;
            setupLink.Content = setupAction == "install" ? "Install Tailscale" : "Continue in Tailscale";
            signIn.IsVisible = setupAction == "signin";
            signIn.IsEnabled = !(busy || !CanConfigure() || setupAction != "signin");
            signIn.Content = pending == "login" ? "Starting sign-in…" : "Sign in to Tailscale";
            setupHelp.Text = setupAction == "approve" ? "Complete the Tailscale approval in your browser, then refresh status here."
                : setupAction == "signin" ? "Sign in with the same Tailscale account you use on your phone or other device."
                : "Install Tailscale on this computer and your phone or other device. Sign in with the same Tailscale account on both.";
            var firstSetup = setup.IsVisible && current.Bool("available") != true && !enabled;
            label.IsVisible = !firstSetup;
            actions.IsVisible = !firstSetup;
            advanced.IsVisible = !(advancedHidden || firstSetup);
            audienceField.IsVisible = !(audienceHidden || firstSetup);
            address.IsVisible = ready;
            install.IsVisible = ready;
            openHref = ready ? url : null;
            copy.IsEnabled = !(!ready || busy);
            if (current is null)
                state.Set(busy ? "Checking private access…" : "Private access unavailable", busy ? "waiting" : "is-disconnected");
            else if (ready) state.Set("Private access connected", "online");
            else if (setupAction == "approve" && setupUrl is not null) state.Set("Waiting for Tailscale approval", "waiting");
            else if (setupAction == "install" && setupUrl is not null) state.Set("Set up Tailscale", "is-disconnected");
            else if (setupAction == "signin") state.Set("Sign in to enable private access", "is-disconnected");
            else if (current.Truthy("error")) state.Set(PrivateAccessError(current["error"]), "is-disconnected");
            else if (current.Bool("available") != true) state.Set("Tailscale is unavailable on this PC.", "is-disconnected");
            else state.Set(enabled ? "Private access is not ready. Refresh status to check it." : "Private access is off", enabled ? "waiting" : "is-disconnected");
        }
        finally { updating = false; }
    }

    Task Start(string operation, bool check = false)
    {
        if (pending is not null) return completion;
        completion = Perform(operation, check);
        return completion;
    }

    async Task Perform(string operation, bool check)
    {
        if (!active || pending is not null) return;
        var saving = operation == "apply";
        var login = operation == "login";
        var wanted = draft ?? current.Bool("enabled");
        var portChange = HasPortChange();
        var wantedPort = JsNumber(portDraft ?? CurrentPort().ToString(CultureInfo.InvariantCulture));
        var audienceChange = HasAudienceChange();
        var wantedAudience = audienceDraft ?? CurrentAudience();
        var wasEnabled = current.Bool("enabled");
        if (saving && (!CanConfigure() || !ValidPort() || portChange && !CanChangePort() || wanted is null ||
                       (wanted == current.Bool("enabled") && !portChange && !audienceChange && !(wanted == true && CanRetry())) ||
                       wanted == true && current.Bool("available") != true))
            return;
        if (login && (!CanConfigure() || current.Str("setup_action") != "signin")) return;
        pending = operation;
        Render();
        Status();
        try
        {
            var payload = new JsonObject { ["enabled"] = wanted };
            if (portChange) payload["https_port"] = wantedPort;
            if (audienceChange) payload["audience"] = wantedAudience;
            var result = saving ? await request("/api/private-access", "PUT", payload)
                : login ? await request("/api/private-access/login", "POST", null)
                : check && CanConfigure() ? await request("/api/private-access/refresh", "POST", null)
                : await request("/api/private-access", "GET", null);
            if (!active) return;
            Update(result);
            var approval = result.Str("setup_action") == "approve" && PrivateAccessSetupUrl(result.Str("action_url"), "approve") is not null &&
                           result.Str("error.code") is "tailscale_signin_required" or "tailscale_approval_required";
            if (approval || !saving && setup.IsVisible && (result.Str("setup_action") == "signin" ||
                                                           result.Str("setup_action") == "install" && PrivateAccessSetupUrl(result.Str("action_url"), "install") is not null))
                Status();
            else if (result.Truthy("error")) Status(PrivateAccessError(result.At("error")), true);
            else if (saving && (result.Bool("enabled") != wanted || portChange && result.Num("https_port") != wantedPort ||
                                audienceChange && result.Str("audience") != wantedAudience))
                Status("The setting could not be confirmed. Refresh status before trying again.", true);
            else if (saving) Status(audienceChange && wanted == wasEnabled ? "Private access updated." : wanted == true ? "Private access enabled." : "Private access disabled.");
            else if (login && result.Str("setup_action") == "approve") Status("Continue in Tailscale to finish signing in, then refresh status.");
        }
        catch (Exception error)
        {
            if (active)
            {
                Status(PrivateAccessError(error), true);
                // The gateway was started by another program, or restarted: show the read-only status.
                if (error is ApiException { Code: "admin_required" })
                {
                    try
                    {
                        var latest = await request("/api/private-access", "GET", null);
                        if (active) Update(latest);
                    }
                    catch (Exception) { }
                }
                if (current is null) state.Set("Private access unavailable", "is-disconnected");
            }
        }
        finally
        {
            pending = null;
            if (active)
            {
                Render();
                if (saving) (toggle.IsEnabled ? (Control)toggle : refresh).Focus();
                else if (login) (setupLink.IsVisible ? setupLink : refresh).Focus();
            }
        }
    }
}
