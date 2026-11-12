using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using EtherlightingEvolved.Native.Core;
using EtherlightingEvolved.Native.Theme;

namespace EtherlightingEvolved.Native.Pages.Settings;

/// <summary>
/// Port of renderPrivateAccess (preview/connections.mjs): the Mobile &amp; remote access section, which turns
/// Tailscale private access on or off, chooses who can open EE, and shows the private address when it is ready.
/// </summary>
public sealed class PrivateAccess : StackPanel
{
    readonly Send request;
    readonly Button refresh = SettingsKit.Button("Refresh private access", "quiet");
    readonly StateLine state = new("Checking private access…", "waiting");
    readonly StackPanel setup = new() { IsVisible = false };
    readonly TextBlock setupHelp = SettingsKit.HelpText("Install Tailscale on this computer and your phone or other device. Sign in with the same Tailscale account on both.");
    readonly Button setupLink = SettingsKit.Button("");
    readonly Button signIn = SettingsKit.Button("Sign in to Tailscale", "primary");
    readonly CheckRow toggleRow = SettingsKit.CheckRow("Use Tailscale");
    readonly CheckBox toggle;
    readonly SelectBox audience = new();
    readonly StackPanel audienceField;
    readonly TextBlock readOnly = SettingsKit.HelpText("Change this setting in EE on the host computer.");
    readonly Button apply = SettingsKit.Button("Apply setting", "primary");
    readonly WrapPanel actions;
    readonly Disclosure advanced;
    readonly TextBox port = SettingsKit.Input();
    readonly Button resetPort = SettingsKit.Button("Reset port", "quiet");
    readonly WrapPanel address;
    readonly Button open = SettingsKit.Button("Open private workspace");
    readonly Button copy = SettingsKit.Button("Copy private address", "quiet");
    readonly TextBlock install = SettingsKit.HelpText("iPhone or iPad: Share → Add to Home Screen. Android: browser menu → Install app.");
    readonly NoticeBox message = new(connection: true);
    JsonObject? current;
    bool? draft;
    string? portDraft, audienceDraft, pending, setupHref, openHref;
    Task completion = Task.CompletedTask;
    bool active = true, rendering;

    public PrivateAccess(Send request)
    {
        this.request = request;
        toggle = toggleRow.Box;
        Spacing = 12;
        var title = SettingsKit.H2("Mobile & remote access", "accent");
        var header = new DockPanel();
        DockPanel.SetDock(refresh, Dock.Right);
        refresh.VerticalAlignment = VerticalAlignment.Top;
        refresh.Margin = new Thickness(18, 0, 0, 0);
        header.Children.Add(refresh);
        header.Children.Add(title);
        var help = SettingsKit.HelpText("Use Tailscale to open this workspace on your phone or another device.");
        setup.Children.Add(setupHelp);
        setup.Children.Add(SettingsKit.Actions(setupLink, signIn));
        audience.SetOptions([("owner", "Only my Tailscale account", false), ("accounts", "Any Tailscale account that can reach this computer", false)]);
        audienceField = SettingsKit.Field("Who can open EE", audience, "Tailscale decides which accounts can reach this computer. Devices without an account stay blocked.");
        readOnly.IsVisible = false;
        actions = SettingsKit.Actions(apply);
        resetPort.IsVisible = false;
        var portField = SettingsKit.Field("Private HTTPS port", port);
        portField.Margin = new Thickness(0, 14, 0, 0);
        portField.MaxWidth = 150;
        portField.HorizontalAlignment = HorizontalAlignment.Left;
        advanced = new Disclosure("Connection options", portField, resetPort, SettingsKit.HelpText("Use a free HTTPS port between 1024 and 65535. Turn off private access before changing it."));
        address = SettingsKit.Actions(open, copy);
        address.IsVisible = false;
        install.IsVisible = false;
        foreach (var child in new Control[] { header, help, state, setup, toggleRow, audienceField, advanced, readOnly, actions, address, install, message }) Children.Add(child);

        toggle.IsCheckedChanged += (_, _) =>
        {
            if (rendering || !active || pending is not null || !CanConfigure()) return;
            var chosen = toggle.IsChecked == true;
            draft = chosen == (current.Bool("enabled") == true) ? null : chosen;
            Status();
            Render();
        };
        audience.Changed += () =>
        {
            if (rendering || !active || pending is not null || !CanConfigure()) return;
            audienceDraft = audience.Value == CurrentAudience() ? null : audience.Value == "accounts" ? "accounts" : "owner";
            Status();
            Render();
        };
        port.TextChanged += (_, _) =>
        {
            if (rendering || !active || pending is not null || !CanChangePort()) return;
            var text = port.Text ?? "";
            portDraft = text == CurrentPort().ToString() ? null : text;
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
        setupLink.Click += (_, _) => { if (setupHref is not null) SettingsKit.OpenExternal(setupHref); };
        open.Click += (_, _) => { if (openHref is not null) SettingsKit.OpenExternal(openHref); };
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
        Render();
        _ = Start("refresh");
    }

    bool CanConfigure() => current.Bool("can_configure") == true;

    bool CanRetry() => current.Bool("enabled") == true && current.Bool("available") == true && current.Bool("ready") != true;

    int CurrentPort() => current.Int("https_port") is { } value && value >= 1024 && value <= 65535 ? value : 8444;

    string? ReadyUrl()
    {
        var url = Connections.PrivateAccessUrl(current.Str("url"));
        return current.Bool("available") == true && current.Bool("enabled") == true && current.Bool("ready") == true && url is not null &&
               Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Port == CurrentPort() ? url : null;
    }

    bool CanChangePort() => CanConfigure() && current.Bool("can_change_port") == true && current.Bool("enabled") != true && current.Bool("ready") != true;

    bool ValidPort()
    {
        var text = portDraft ?? CurrentPort().ToString();
        return Regex.IsMatch(text, @"^\d+$") && double.TryParse(text, out var value) && value >= 1024 && value <= 65535;
    }

    static double PortNumber(string text) => Regex.IsMatch(text.Trim(), @"^\d+$") && double.TryParse(text.Trim(), out var value) ? value : double.NaN;

    bool HasPortChange() => portDraft is not null && PortNumber(portDraft) != CurrentPort();

    string CurrentAudience() => current.Str("audience") == "accounts" ? "accounts" : "owner";

    bool HasAudienceChange() => audienceDraft is not null && audienceDraft != CurrentAudience();

    void Status(string text = "", bool error = false, string tone = "") => message.Set(text, error, tone);

    void Render()
    {
        if (!active) return;
        rendering = true;
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
            apply.SetLabel(pending == "apply" ? "Applying…" : chosen == enabled && CanRetry() ? "Retry private access" : "Apply setting");
            refresh.IsEnabled = !busy;
            readOnly.IsVisible = !(current is null || CanConfigure());
            var portText = portDraft ?? CurrentPort().ToString();
            if (port.Text != portText) port.Text = portText;
            port.IsEnabled = !(busy || !CanChangePort());
            var advancedHidden = current is null || !CanConfigure();
            resetPort.IsVisible = portDraft is not null;
            resetPort.IsEnabled = !(busy || !CanConfigure());
            var setupAction = current.Str("setup_action");
            var setupUrl = Connections.PrivateAccessSetupUrl(current.Str("action_url"), setupAction);
            setup.IsVisible = CanConfigure() && setupAction is "install" or "signin" or "approve";
            setupLink.IsVisible = setupUrl is not null;
            setupHref = setupUrl;
            setupLink.SetLabel(setupAction == "install" ? "Install Tailscale" : "Continue in Tailscale");
            signIn.IsVisible = setupAction == "signin";
            signIn.IsEnabled = !(busy || !CanConfigure() || setupAction != "signin");
            signIn.SetLabel(pending == "login" ? "Starting sign-in…" : "Sign in to Tailscale");
            setupHelp.Text = setupAction == "approve" ? "Complete the Tailscale approval in your browser, then refresh status here."
                : setupAction == "signin" ? "Sign in with the same Tailscale account you use on your phone or other device."
                : "Install Tailscale on this computer and your phone or other device. Sign in with the same Tailscale account on both.";
            var firstSetup = setup.IsVisible && current.Bool("available") != true && !enabled;
            toggleRow.IsVisible = !firstSetup;
            actions.IsVisible = !firstSetup;
            advanced.IsVisible = !(advancedHidden || firstSetup);
            audienceField.IsVisible = !(audienceHidden || firstSetup);
            address.IsVisible = ready;
            install.IsVisible = ready;
            openHref = ready ? url : null;
            copy.IsEnabled = !(!ready || busy);
            if (current is null) state.Set(busy ? "Checking private access…" : "Private access unavailable", busy ? "waiting" : "is-disconnected");
            else if (ready) state.Set("Private access connected", "online");
            else if (setupAction == "approve" && setupUrl is not null) state.Set("Waiting for Tailscale approval", "waiting");
            else if (setupAction == "install" && setupUrl is not null) state.Set("Set up Tailscale", "is-disconnected");
            else if (setupAction == "signin") state.Set("Sign in to enable private access", "is-disconnected");
            else if (current.Truthy("error")) state.Set(Connections.PrivateAccessError(Problem.From(current["error"])), "is-disconnected");
            else if (current.Bool("available") != true) state.Set("Tailscale is unavailable on this PC.", "is-disconnected");
            else state.Set(enabled ? "Private access is not ready. Refresh status to check it." : "Private access is off", enabled ? "waiting" : "is-disconnected");
        }
        finally { rendering = false; }
    }

    /// <summary>Accepts a private access status; anything without the three booleans is refused.</summary>
    public void Update(JsonNode? value)
    {
        if (!active) return;
        if (value is not JsonObject status || status.Bool("enabled") is null || status.Bool("available") is null || status.Bool("ready") is null)
            throw new InvalidOperationException("invalid_private_status");
        current = status;
        if (draft == (current.Bool("enabled") == true)) draft = null;
        if (portDraft is not null && PortNumber(portDraft) == CurrentPort()) portDraft = null;
        if (audienceDraft == CurrentAudience()) audienceDraft = null;
        Render();
    }

    async Task Perform(string operation, bool check)
    {
        if (!active || pending is not null) return;
        var saving = operation == "apply";
        var login = operation == "login";
        var wanted = draft ?? current.Bool("enabled");
        var portChange = HasPortChange();
        var wantedPort = PortNumber(portDraft ?? CurrentPort().ToString());
        var audienceChange = HasAudienceChange();
        var wantedAudience = audienceDraft ?? CurrentAudience();
        var wasEnabled = current.Bool("enabled");
        if (saving && (!CanConfigure() || !ValidPort() || portChange && !CanChangePort() || wanted is null ||
                       (wanted == current.Bool("enabled") && !portChange && !audienceChange && !(wanted == true && CanRetry())) || wanted == true && current.Bool("available") != true))
            return;
        if (login && (!CanConfigure() || current.Str("setup_action") != "signin")) return;
        pending = operation;
        Render();
        Status();
        try
        {
            var payload = new JsonObject { ["enabled"] = wanted };
            if (portChange) payload["https_port"] = (int)wantedPort;
            if (audienceChange) payload["audience"] = wantedAudience;
            var result = saving ? await request("/api/private-access", "PUT", payload)
                : login ? await request("/api/private-access/login", "POST")
                : check && CanConfigure() ? await request("/api/private-access/refresh", "POST")
                : await request("/api/private-access");
            if (!active) return;
            Update(result);
            var approval = result.Str("setup_action") == "approve" && Connections.PrivateAccessSetupUrl(result.Str("action_url"), "approve") is not null &&
                           result.Str("error.code") is "tailscale_signin_required" or "tailscale_approval_required";
            if (approval || !saving && setup.IsVisible && (result.Str("setup_action") == "signin" ||
                                                          result.Str("setup_action") == "install" && Connections.PrivateAccessSetupUrl(result.Str("action_url"), "install") is not null))
                Status();
            else if (result.Truthy("error")) Status(Connections.PrivateAccessError(Problem.From(result?["error"])), true);
            else if (saving && (result.Bool("enabled") != wanted || portChange && result.Num("https_port") != wantedPort || audienceChange && result.Str("audience") != wantedAudience))
                Status("The setting could not be confirmed. Refresh status before trying again.", true);
            else if (saving) Status(audienceChange && wanted == wasEnabled ? "Private access updated." : wanted == true ? "Private access enabled." : "Private access disabled.");
            else if (login && result.Str("setup_action") == "approve") Status("Continue in Tailscale to finish signing in, then refresh status.");
        }
        catch (Exception exception)
        {
            if (active)
            {
                Status(Connections.PrivateAccessError(Problem.From(exception)), true);
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

    Task Start(string operation, bool check = false)
    {
        if (pending is not null) return completion;
        completion = Perform(operation, check);
        return completion;
    }

    public Task Refresh() => Start("refresh", true);

    public bool HasChanges => draft is not null || portDraft is not null || audienceDraft is not null;

    public async Task<bool> CanLeave()
    {
        try { await completion; } catch (Exception) { }
        return true;
    }

    public void Close() => active = false;
}
